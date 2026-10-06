using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecureFact.Api.Infrastructure;

namespace SecureFact.Security.Tests;

/// <summary>An unhandled exception becomes a problem document with a stable code and a trace id, and never shows what went wrong inside.</summary>
public sealed class GlobalExceptionHandlerTests
{
    private sealed class RecordingLogger : ILogger<GlobalExceptionHandler>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }

    private static DefaultHttpContext NewContext(CancellationToken aborted = default)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddOptions().BuildServiceProvider(),
            RequestAborted = aborted,
        };
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/documents";
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-for-the-test";
        return context;
    }

    private static JsonElement Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body).RootElement.Clone();
    }

    [Fact]
    public async Task An_unexpected_exception_is_a_500_with_a_code_and_a_trace_and_no_internal_detail()
    {
        var logger = new RecordingLogger();
        var context = NewContext();
        var secret = new InvalidOperationException("password=hunter2 Host=db.internal at SecureFact.Billing.DocumentService.IssueAsync");

        var handled = await new GlobalExceptionHandler(logger).TryHandleAsync(context, secret, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(500, context.Response.StatusCode);
        var body = Body(context);
        Assert.Equal("SF-SYS-001", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        var text = body.GetRawText();
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("db.internal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentService", text, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", text, StringComparison.Ordinal);
        var logged = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, logged.Level);
        Assert.Same(secret, logged.Exception); // the detail goes to the log, where an operator can read it
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    public async Task A_malformed_request_keeps_its_status_and_says_it_is_invalid(int status)
    {
        var context = NewContext();

        var handled = await new GlobalExceptionHandler(new RecordingLogger()).TryHandleAsync(context, new BadHttpRequestException("Unexpected end of request content.", status), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(status, context.Response.StatusCode);
        var body = Body(context);
        Assert.Equal("SF-VAL-005", body.GetProperty("code").GetString());
        Assert.DoesNotContain("Unexpected end", body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancellation_by_the_caller_is_not_an_error_but_any_other_cancellation_is()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var gone = NewContext(aborted.Token);
        var logger = new RecordingLogger();

        Assert.True(await new GlobalExceptionHandler(logger).TryHandleAsync(gone, new OperationCanceledException(), CancellationToken.None));
        Assert.Equal(0, gone.Response.Body.Length); // nobody is listening: nothing written
        Assert.Empty(logger.Entries);

        var internalTimeout = NewContext();
        Assert.True(await new GlobalExceptionHandler(logger).TryHandleAsync(internalTimeout, new OperationCanceledException(), CancellationToken.None));
        Assert.Equal(500, internalTimeout.Response.StatusCode);
        Assert.Single(logger.Entries);
    }
}
