using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SecureFact.Identity.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>Captures password-reset tokens that a real notifier would e-mail, so tests can complete the flow.</summary>
public sealed class CapturingNotifier : IPasswordResetNotifier
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public Task SendAsync(string email, string token, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        lock (_tokens)
        {
            _tokens[email] = token;
        }

        return Task.CompletedTask;
    }

    public string? TokenFor(string email)
    {
        lock (_tokens)
        {
            return _tokens.GetValueOrDefault(email);
        }
    }
}

public sealed record TestUser(string Email, string Password, Guid Id);

public sealed class ApiFixture : IAsyncLifetime
{
    public const string AdminEmail = "platform.admin@securefact.test";
    public const string AdminPassword = "Platform-admin passphrase 2026";
    public const string StrongPassword = "A long and unusual passphrase 42";

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;

    public ApiFixture()
    {
        _postgres = new PostgresFixture();
    }

    public CapturingNotifier Notifier { get; } = new();

    public LogSink Logs { get; } = new();

    public FakeSunatChannel Sunat { get; } = new();

    public PostgresFixture Postgres => _postgres;

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

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IPasswordResetNotifier>(Notifier);
                services.AddSingleton<SecureFact.CpeEngine.Contracts.ICpeSubmissionChannel>(Sunat);
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
public sealed class ApiTestGroup : ICollectionFixture<ApiFixture>, ICollectionFixture<RabbitFixture>
{
    public const string Name = "api";
}
