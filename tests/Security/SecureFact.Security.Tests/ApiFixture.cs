using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications.Application;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The outgoing e-mail of the tests: keeps what would leave, so a test reads the message the real notifier composed.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];

    /// <summary>While true, the channel fails as an SMTP server that is down would.</summary>
    public bool Fail { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new EmailDeliveryException("simulated: the mail server is down");
        }

        lock (_sent)
        {
            _sent.Add(message);
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<EmailMessage> To(string email)
    {
        lock (_sent)
        {
            return _sent.Where(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }
}

/// <summary>Reads the password-reset token out of the e-mail that the real notifier sent, so tests complete the flow exactly as a person does: from the link.</summary>
public sealed partial class CapturingNotifier(CapturingEmailSender mail)
{
    [GeneratedRegex(@"#token=([^\s""<]+)")]
    private static partial Regex TokenPattern();

    public string? TokenFor(string email) =>
        mail.To(email).Select(m => TokenPattern().Match(m.Text)).LastOrDefault(m => m.Success) is { } match ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
}

public sealed record TestUser(string Email, string Password, Guid Id);

/// <summary>
/// The clock of the services of the API under test. It is the real one until a test moves it with <see cref="At"/>, to run what happens months later (the charges of a month that closed) through the
/// real services. A test that moves it does so for a call and gives it back, and the tests of the collection run one after another, so none sees another's time.
/// </summary>
public sealed class ShiftedClock : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    /// <summary>From now until the result is disposed, the services believe it is <paramref name="when"/>.</summary>
    public IDisposable At(DateTimeOffset when)
    {
        Interlocked.Exchange(ref _offsetTicks, (when - base.GetUtcNow()).Ticks);
        return new Back(this);
    }

    private sealed class Back(ShiftedClock clock) : IDisposable
    {
        public void Dispose() => Interlocked.Exchange(ref clock._offsetTicks, 0);
    }
}

public sealed class ApiFixture : IAsyncLifetime
{
    public const string AdminEmail = "platform.admin@securefact.test";
    public const string AdminPassword = "Platform-admin passphrase 2026";
    public const string PublicUrl = "https://app.securefact.test";
    public const string StrongPassword = "A long and unusual passphrase 42";

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;

    public ApiFixture()
    {
        _postgres = new PostgresFixture();
    }

    public CapturingEmailSender Mail { get; } = new();

    public CapturingNotifier Notifier => new(Mail);

    public LogSink Logs { get; } = new();

    public FakeSunatChannel Sunat { get; } = new();

    public FakeGreChannel Gre { get; } = new();

    /// <summary>The DNS that the verification of the domains asks: what a test publishes is what it finds.</summary>
    public FakeDomainNameSystem Dns { get; } = new();

    public PostgresFixture Postgres => _postgres;

    public ShiftedClock Clock { get; } = new();

    public IServiceProvider Services => _factory!.Services;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();

        var apiConnection = new NpgsqlConnectionStringBuilder(_postgres.AppConnectionString) { MaxPoolSize = 30 }.ConnectionString;
        Environment.SetEnvironmentVariable("ConnectionStrings__App", apiConnection);
        Environment.SetEnvironmentVariable("Identity__SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("Identity__Pbkdf2Iterations", "1000");
        Environment.SetEnvironmentVariable("Identity__MaxFailedAttempts", "5");
        Environment.SetEnvironmentVariable("Security__LocalDevKek", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("RateLimiting__AuthPermitPerMinute", "100000");
        // The domains of the resellers (ADR-051): the platform's edge and its own hosts, the secret of the edge and a check that may be repeated after two seconds.
        Environment.SetEnvironmentVariable("Domains__EdgeHost", FakeDomainNameSystem.EdgeHost);
        Environment.SetEnvironmentVariable("Domains__EdgeAddresses__0", FakeDomainNameSystem.EdgeAddress);
        Environment.SetEnvironmentVariable("Domains__PlatformHosts__0", FakeDomainNameSystem.PlatformHost);
        Environment.SetEnvironmentVariable("Domains__EdgeSecret", FakeDomainNameSystem.EdgeSecret);
        Environment.SetEnvironmentVariable("Domains__MinimumCheckSeconds", "2");
        // The webhooks of the tests point at a receiver on this machine (ADR-067); the API refuses that in production and the tests that check the refusal turn it off.
        Environment.SetEnvironmentVariable("Webhooks__AllowLocalTargets", "true");
        Environment.SetEnvironmentVariable("Web__PublicUrl", PublicUrl);
        // A channel is configured so that the notices are queued (ADR-054); the sender itself is the one of the tests, which keeps what would leave.
        Environment.SetEnvironmentVariable("Email__Provider", "Sandbox");
        Environment.SetEnvironmentVariable("Email__From", "no-responder@securefact.test");
        Environment.SetEnvironmentVariable("Email__Sandbox__Directory", Path.Combine(Path.GetTempPath(), "sf-test-mail-unused"));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<IEmailSender>(Mail);
                services.AddSingleton<SecureFact.CpeEngine.Contracts.ICpeSubmissionChannel>(Sunat);
                services.AddSingleton<SecureFact.Gre.Contracts.IGreChannel>(Gre);
                services.AddSingleton<SecureFact.Tenancy.Contracts.IDomainNameSystem>(Dns);
                services.AddLogging(logging => logging.AddProvider(Logs));
            }));

        await using var scope = _factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IPlatformBootstrapper>()
            .EnsureFirstPlatformAdminAsync(AdminEmail, AdminPassword, CancellationToken.None);
        Assert.True(created.IsSuccess);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public HttpClient NewClient() => _factory!.CreateClient();

    /// <summary>Runs the dispatcher of the queued e-mails until the queue has nothing due (the worker does this in a real deployment).</summary>
    public async Task DrainMailAsync()
    {
        EmailDispatchReport report;
        do
        {
            await using var scope = _factory!.Services.CreateAsyncScope();
            report = await scope.ServiceProvider.GetRequiredService<IEmailDispatcher>().RunOnceAsync(CancellationToken.None);
        }
        while (report.Sent > 0);
    }

    public async Task<EmailDispatchReport> DispatchMailOnceAsync()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailDispatcher>().RunOnceAsync(CancellationToken.None);
    }

    /// <summary>A client that keeps no cookies of its own: a test that sends and reads the cookies by hand sees exactly what the server says.</summary>
    public HttpClient NewCookielessClient() => _factory!.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });

    public async Task<HttpResponseMessage> LoginAsync(string email, string password, string? totp = null)
    {
        using var client = NewClient();
        return await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password, totpCode = totp });
    }

    public async Task<AuthTokens> LoginOkAsync(string email, string password, string? totp = null)
    {
        var response = await LoginAsync(email, password, totp);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthTokens>(JsonOptions))!;
    }

    public HttpClient ClientFor(AuthTokens tokens)
    {
        var client = NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    public async Task<HttpClient> AdminClientAsync() => ClientFor(await LoginOkAsync(AdminEmail, AdminPassword));

    public async Task<Guid> CreateTenantAsync(string name)
    {
        using var admin = await AdminClientAsync();
        var response = await admin.PostAsJsonAsync("/api/v1/platform/tenants", new { name, environment = "Sandbox" });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Guid.Parse(body.RootElement.GetProperty("id").GetString()!);
    }

    /// <summary>Creates a user through the real API as the given actor.</summary>
    public static async Task<TestUser> CreateUserAsync(HttpClient actor, string role, Guid? tenantId, string? email = null)
    {
        email ??= $"{Guid.NewGuid():N}@securefact.test";
        var response = await actor.PostAsJsonAsync("/api/v1/users", new
        {
            email,
            displayName = "Test User",
            password = StrongPassword,
            roles = new[] { role },
            tenantId,
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<UserDto>(JsonOptions))!;
        return new TestUser(email, StrongPassword, dto.Id);
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFixture>, ICollectionFixture<RabbitFixture>, ICollectionFixture<S3Fixture>
{
    public const string Name = "api";
}
