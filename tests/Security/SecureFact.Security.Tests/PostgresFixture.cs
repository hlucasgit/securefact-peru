using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SecureFact.Platform;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Audit;
using SecureFact.Billing;
using SecureFact.Catalogs;
using SecureFact.Identity;
using SecureFact.Customers;
using SecureFact.Organizations;
using SecureFact.Products;
using SecureFact.Rules;
using SecureFact.Tenancy;
using Testcontainers.PostgreSql;

namespace SecureFact.Security.Tests;

/// <summary>
/// One real PostgreSQL per test run. Mirrors production roles: a privileged owner that applies migrations and a
/// runtime role (<c>securefact_app</c>) with neither ownership nor BYPASSRLS, which is the only role the tests act as.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string AppPassword = "app-test-password";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public string OwnerConnectionString => _container.GetConnectionString();

    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await ExecuteAsOwnerAsync($"CREATE ROLE {RlsSql.AppRole} LOGIN PASSWORD '{AppPassword}' NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE");

        var builder = new NpgsqlConnectionStringBuilder(OwnerConnectionString)
        {
            Username = RlsSql.AppRole,
            Password = AppPassword,
            MaxPoolSize = 1,
        };
        AppConnectionString = builder.ConnectionString;

        await TenancyModule.MigrateAsync(OwnerConnectionString);
        await IdentityModule.MigrateAsync(OwnerConnectionString);
        await AuditModule.MigrateAsync(OwnerConnectionString);
        await OrganizationsModule.MigrateAsync(OwnerConnectionString);
        await BillingModule.MigrateAsync(OwnerConnectionString);
        await CatalogsModule.MigrateAsync(OwnerConnectionString);
        await RulesModule.MigrateAsync(OwnerConnectionString);
        await CustomersModule.MigrateAsync(OwnerConnectionString);
        await SecureFact.Certificates.CertificatesModule.MigrateAsync(OwnerConnectionString);
        await SecureFact.CpeEngine.CpeEngineModule.MigrateAsync(OwnerConnectionString);
        await ProductsModule.MigrateAsync(OwnerConnectionString);

        await ExecuteAsOwnerAsync($"""
            CREATE SCHEMA rlstest;
            CREATE TABLE rlstest.notes (id uuid PRIMARY KEY, tenant_id uuid NOT NULL, body text NOT NULL);
            CREATE TABLE rlstest.platform_notes (id uuid PRIMARY KEY, tenant_id uuid NOT NULL, body text NOT NULL);
            {RlsSql.Enable("rlstest", "notes")}
            {RlsSql.Enable("rlstest", "platform_notes", RlsMode.TenantOrPlatform)}
            {RlsSql.GrantToAppRole("rlstest", "SELECT, INSERT, UPDATE, DELETE")}
            """);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task ExecuteAsOwnerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsOwnerAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public NotesDbContext NotesContext(DataScope scope)
    {
        var options = new DbContextOptionsBuilder<NotesDbContext>()
            .UseNpgsql(AppConnectionString)
            .AddInterceptors(new RlsConnectionInterceptor(scope))
            .Options;
        return new NotesDbContext(options, scope);
    }

    public ServiceProvider BuildServices() => new ServiceCollection()
        .AddLogging()
        .AddPlatformDataScope()
        .AddScoped<ICurrentUser, AnonymousCurrentUser>()
        .AddScoped<IRequestContext, NoRequestContext>()
        .AddAuditModule(AppConnectionString)
        .AddTenancyModule(AppConnectionString)
        .BuildServiceProvider(validateScopes: true);

    public static DataScope TenantScope(TenantId tenant)
    {
        var scope = new DataScope();
        scope.UseTenant(tenant);
        return scope;
    }

    public static DataScope PlatformScope()
    {
        var scope = new DataScope();
        scope.UsePlatform("security tests");
        return scope;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

public sealed class Note : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public string Body { get; set; } = string.Empty;
}

/// <summary>A row of the tenant or of the platform itself (null tenant), as the users of the Identity module are.</summary>
public sealed class PlatformNote : IOptionalTenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid? TenantId { get; set; }

    public string Body { get; set; } = string.Empty;
}

public sealed class NotesDbContext(DbContextOptions<NotesDbContext> options, IDataScope scope) : TenantDbContext(options, scope)
{
    public DbSet<Note> Notes => Set<Note>();

    public DbSet<PlatformNote> PlatformNotes => Set<PlatformNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Note>(builder =>
        {
            builder.ToTable("notes", "rlstest");
            builder.HasKey(n => n.Id);
            builder.Property(n => n.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(n => n.Body).HasColumnName("body");
            ConfigureTenantOwned(builder);
        });
        modelBuilder.Entity<PlatformNote>(builder =>
        {
            builder.ToTable("platform_notes", "rlstest");
            builder.HasKey(n => n.Id);
            builder.Property(n => n.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(n => n.Body).HasColumnName("body");
            ConfigureOptionalTenantOwned(builder);
        });
    }
}

/// <summary>Stand-ins for what the HTTP host provides, for tests that exercise modules without the API.</summary>
public sealed class AnonymousCurrentUser : ICurrentUser
{
    public bool IsAuthenticated => false;

    public Guid? UserId => null;

    public Guid? SessionId => null;

    public TenantId? TenantId => null;

    public bool IsPlatform => false;

    public Guid? ResellerId => null;

    public IReadOnlySet<string> Roles { get; } = new HashSet<string>();

    public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();

    public bool HasPermission(string permission) => false;
}

public sealed class NoRequestContext : IRequestContext
{
    public string? IpAddress => null;

    public string? UserAgent => null;

    public string? CorrelationId => null;

    public string? RequestId => null;
}
