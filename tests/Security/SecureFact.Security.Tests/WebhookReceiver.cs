using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SecureFact.Security.Tests;

/// <summary>
/// An address that a webhook points at, listening on the machine of the test (the API of the tests is configured to allow local targets). It keeps every request it gets and answers with the
/// status the test sets, so a test sees exactly what a customer's server would.
/// </summary>
public sealed class WebhookReceiver : IAsyncDisposable
{
    public sealed record Received(string Path, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;
    }

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Received> _requests = new();
    private int _status = 200;

    private WebhookReceiver(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    /// <summary>The status that the next requests are answered with.</summary>
    public int Status
    {
        get => Volatile.Read(ref _status);
        set => Volatile.Write(ref _status, value);
    }

    public IReadOnlyList<Received> Requests => [.. _requests];

    public static async Task<WebhookReceiver> StartAsync(string path = "hook")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        WebhookReceiver? receiver = null;
        app.MapPost("/{**path}", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            receiver!._requests.Enqueue(new Received(context.Request.Path, context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));
            return Results.StatusCode(receiver.Status);
        });
        await app.StartAsync();
        receiver = new WebhookReceiver(app, app.Urls.First().TrimEnd('/') + "/" + path);
        return receiver;
    }

    /// <summary>What a receiver does to trust a delivery: recompute the signature with the secret and compare. Written here from the documented rule, not with the code of the platform.</summary>
    public static bool IsAuthentic(Received request, string secret)
    {
        var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{request.Header("X-SecureFact-Timestamp")}.{request.Body}")));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(request.Header("X-SecureFact-Signature")));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
