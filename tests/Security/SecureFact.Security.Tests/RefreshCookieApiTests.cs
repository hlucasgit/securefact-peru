using System.Net;
using System.Net.Http.Json;
using SecureFact.Identity.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The refresh token in an HttpOnly cookie (ADR-050): where it goes, what spends it, and that the clients of the API that do not ask for it keep the body.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RefreshCookieApiTests(ApiFixture api)
{
    private const string Header = "X-SecureFact-Session";

    private sealed record Issued(string AccessToken, string RefreshToken);

    private async Task<(string Email, string Password)> NewUserAsync(string name)
    {
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        return (user.Email, user.Password);
    }

    private static HttpRequestMessage Post(string path, object? body = null, bool cookieMode = true, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (cookieMode)
        {
            request.Headers.Add(Header, "cookie");
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"sf_rt={cookie}");
        }

        return request;
    }

    /// <summary>The value of the refresh cookie that a response sets, or null.</summary>
    private static string? CookieValue(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith("sf_rt=", StringComparison.Ordinal)).Select(v => v["sf_rt=".Length..].Split(';')[0]).FirstOrDefault(v => v.Length > 0)
            : null;

    private static string RawCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("sf_rt=", StringComparison.Ordinal));

    [Fact]
    public async Task In_the_cookie_mode_the_refresh_token_goes_in_an_httponly_strict_cookie_and_not_in_the_body()
    {
        var (email, password) = await NewUserAsync("Cookie SAC");
        using var client = api.NewCookielessClient();

        var login = await client.SendAsync(Post("/api/v1/auth/login", new { email, password }));

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = (await login.Content.ReadFromJsonAsync<Issued>(ApiFixture.JsonOptions))!;
        Assert.False(string.IsNullOrEmpty(body.AccessToken));
        Assert.True(string.IsNullOrEmpty(body.RefreshToken)); // the page never sees it
        var cookie = RawCookie(login);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase); // a session cookie, as the storage of the tab was
        Assert.DoesNotContain("max-age", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrEmpty(CookieValue(login)));
    }

    [Fact]
    public async Task The_cookie_renews_the_session_and_rotates_with_every_use()
    {
        var (email, password) = await NewUserAsync("Rotación SAC");
        using var client = api.NewCookielessClient();
        var first = CookieValue(await client.SendAsync(Post("/api/v1/auth/login", new { email, password })))!;

        var refreshed = await client.SendAsync(Post("/api/v1/auth/refresh", cookie: first));

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = CookieValue(refreshed)!;
        Assert.NotEqual(first, second);
        var tokens = (await refreshed.Content.ReadFromJsonAsync<Issued>(ApiFixture.JsonOptions))!;
        Assert.True(string.IsNullOrEmpty(tokens.RefreshToken));
        using var authorized = api.ClientFor(new AuthTokens(tokens.AccessToken, string.Empty, 900));
        Assert.Equal(HttpStatusCode.OK, (await authorized.GetAsync("/api/v1/companies")).StatusCode);

        // The rotated cookie goes on; the old one, already spent, is a reuse, which closes the whole session.
        var third = CookieValue(await client.SendAsync(Post("/api/v1/auth/refresh", cookie: second)));
        Assert.NotNull(third);
        var reuse = await client.SendAsync(Post("/api/v1/auth/refresh", cookie: first));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Contains("expires=", RawCookie(reuse), StringComparison.OrdinalIgnoreCase); // and the cookie is cleared
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post("/api/v1/auth/refresh", cookie: third))).StatusCode);
    }

    [Fact]
    public async Task A_cookie_without_the_header_is_not_spent_and_a_missing_or_bad_cookie_is_refused_and_cleared()
    {
        var (email, password) = await NewUserAsync("Sin encabezado SAC");
        using var client = api.NewCookielessClient();
        var cookie = CookieValue(await client.SendAsync(Post("/api/v1/auth/login", new { email, password })))!;

        // A request that the page did not make (a form, another site) cannot add the header, so the cookie it carries is worth nothing.
        var withoutHeader = await client.SendAsync(Post("/api/v1/auth/refresh", cookieMode: false, cookie: cookie));
        Assert.True(withoutHeader.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest);
        Assert.Null(CookieValue(withoutHeader));

        foreach (var bad in new string?[] { null, "no-es-un-token", "AAAA" })
        {
            var refused = await client.SendAsync(Post("/api/v1/auth/refresh", cookie: bad));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Contains("expires=", RawCookie(refused), StringComparison.OrdinalIgnoreCase);
        }

        // The refusals did not touch the real session.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/api/v1/auth/refresh", cookie: cookie))).StatusCode);
    }

    [Fact]
    public async Task The_clients_of_the_api_that_do_not_ask_for_the_cookie_keep_the_token_in_the_body_and_get_no_cookie()
    {
        var (email, password) = await NewUserAsync("Cliente de API SAC");
        using var client = api.NewCookielessClient();

        var login = await client.SendAsync(Post("/api/v1/auth/login", new { email, password }, cookieMode: false));

        var body = (await login.Content.ReadFromJsonAsync<Issued>(ApiFixture.JsonOptions))!;
        Assert.False(string.IsNullOrEmpty(body.RefreshToken));
        Assert.Null(CookieValue(login));
        var refreshed = await client.SendAsync(Post("/api/v1/auth/refresh", new { refreshToken = body.RefreshToken }, cookieMode: false));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.False(string.IsNullOrEmpty((await refreshed.Content.ReadFromJsonAsync<Issued>(ApiFixture.JsonOptions))!.RefreshToken));
        Assert.Null(CookieValue(refreshed));
    }

    [Fact]
    public async Task Logging_out_clears_the_cookie_and_the_session_does_not_come_back()
    {
        var (email, password) = await NewUserAsync("Salida SAC");
        using var client = api.NewCookielessClient();
        var login = await client.SendAsync(Post("/api/v1/auth/login", new { email, password }));
        var cookie = CookieValue(login)!;
        var access = (await login.Content.ReadFromJsonAsync<Issued>(ApiFixture.JsonOptions))!.AccessToken;

        using var logout = Post("/api/v1/auth/logout");
        logout.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        var response = await client.SendAsync(logout);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("expires=", RawCookie(response), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post("/api/v1/auth/refresh", cookie: cookie))).StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_in_the_cookie_mode_sets_no_cookie()
    {
        var (email, _) = await NewUserAsync("Clave mala SAC");
        using var client = api.NewCookielessClient();

        var login = await client.SendAsync(Post("/api/v1/auth/login", new { email, password = "Esta no es la clave 99" }));

        Assert.NotEqual(HttpStatusCode.OK, login.StatusCode);
        Assert.Null(CookieValue(login));
    }

    [Fact]
    public async Task The_cookie_is_secure_over_https()
    {
        var (email, password) = await NewUserAsync("Seguro SAC");
        using var client = api.NewCookielessClient();
        client.BaseAddress = new Uri("https://localhost");

        var login = await client.SendAsync(Post("/api/v1/auth/login", new { email, password }));

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("secure", RawCookie(login).Replace("samesite", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    }
}
