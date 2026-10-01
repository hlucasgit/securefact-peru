using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Security.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class TenantAdministrationTests(PostgresFixture postgres)
{
    private static readonly CreateTenantRequest Valid = new("Acme Perú SAC", TenantEnvironment.Sandbox);

    private async Task<TenantDto> CreateAsPlatformAsync(string name)
    {
        await using var services = postgres.BuildServices();
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("create tenant in test");

        var result = await scope.ServiceProvider.GetRequiredService<ITenantAdministration>()
            .CreateAsync(Valid with { Name = name }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<Result<T>> RunAsync<T>(Action<DataScope> configure, Func<ITenantAdministration, Task<Result<T>>> action)
    {
        await using var services = postgres.BuildServices();
        await using var scope = services.CreateAsyncScope();
        configure(scope.ServiceProvider.GetRequiredService<DataScope>());
        return await action(scope.ServiceProvider.GetRequiredService<ITenantAdministration>());
    }

    [Fact]
    public async Task Platform_scope_creates_a_tenant()
    {
        var tenant = await CreateAsPlatformAsync("Create Test SAC");

        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal("Create Test SAC", tenant.Name);
    }

    [Fact]
    public async Task Tenant_and_anonymous_scopes_cannot_create_tenants()
    {
        var asTenant = await RunAsync(s => s.UseTenant(TenantId.New()), api => api.CreateAsync(Valid, CancellationToken.None));
        var asAnonymous = await RunAsync(_ => { }, api => api.CreateAsync(Valid, CancellationToken.None));

        Assert.Equal(ErrorCodes.Forbidden, asTenant.Error.Code);
        Assert.Equal(ErrorCodes.Forbidden, asAnonymous.Error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    public async Task Invalid_names_are_rejected(string name)
    {
        var result = await RunAsync(s => s.UsePlatform("test"), api => api.CreateAsync(Valid with { Name = name }, CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidTenantName, result.Error.Code);
    }

    [Fact]
    public async Task A_tenant_can_read_itself_but_not_another_tenant()
    {
        var mine = await CreateAsPlatformAsync("Mine SAC");
        var other = await CreateAsPlatformAsync("Other SAC");

        var self = await RunAsync(s => s.UseTenant(mine.Id), api => api.GetAsync(mine.Id, CancellationToken.None));
        var foreign = await RunAsync(s => s.UseTenant(mine.Id), api => api.GetAsync(other.Id, CancellationToken.None));

        Assert.True(self.IsSuccess);
        Assert.Equal(ErrorCodes.TenantNotFound, foreign.Error.Code);
    }

    [Fact]
    public async Task Platform_scope_reads_any_tenant_and_anonymous_reads_none()
    {
        var tenant = await CreateAsPlatformAsync("Visible SAC");

        var asPlatform = await RunAsync(s => s.UsePlatform("test"), api => api.GetAsync(tenant.Id, CancellationToken.None));
        var asAnonymous = await RunAsync(_ => { }, api => api.GetAsync(tenant.Id, CancellationToken.None));

        Assert.True(asPlatform.IsSuccess);
        Assert.Equal(ErrorCodes.TenantNotFound, asAnonymous.Error.Code);
    }

    [Fact]
    public async Task Database_blocks_a_tenant_from_registering_tenants_even_without_the_service()
    {
        await using var connection = new NpgsqlConnection(postgres.AppConnectionString);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", TenantId.New().Value.ToString("D"));
            await set.ExecuteNonQueryAsync();
        }

        await using var insert = new NpgsqlCommand(
            "INSERT INTO tenancy.tenant (id, name, status, environment, created_at) VALUES (gen_random_uuid(), 'Rogue', 'Active', 'Production', now())",
            connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }
}
