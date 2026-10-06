using System.Net;
using System.Net.Http.Json;
using SecureFact.Identity.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>
/// Found by the end-to-end tests of the web interface (ADR-040): several sign-ins of the same account at once updated the same user row and the losers failed with a 500.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ConcurrentSessionsTests(ApiFixture api)
{
    [Fact]
    public async Task Many_sign_ins_of_the_same_account_at_once_all_succeed()
    {
        var tenantId = await api.CreateTenantAsync("Concurrent Login SAC");
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => api.LoginAsync(user.Email, user.Password)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var tokens = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<AuthTokens>(ApiFixture.JsonOptions)));
        Assert.Equal(12, tokens.Select(token => token!.RefreshToken).Distinct().Count());
    }

    [Fact]
    public async Task Exchanging_the_same_refresh_token_at_once_never_fails_with_a_server_error()
    {
        var tenantId = await api.CreateTenantAsync("Concurrent Refresh SAC");
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        var tokens = await api.LoginOkAsync(user.Email, user.Password);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var client = api.NewClient();
            return await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });
        }));

        Assert.All(responses, response => Assert.True((int)response.StatusCode < 500, $"{(int)response.StatusCode}"));
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
    }
}
