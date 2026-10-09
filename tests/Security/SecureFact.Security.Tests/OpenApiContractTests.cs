using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SecureFact.Security.Tests;

/// <summary>
/// The description of the API is a contract (ADR-066): what integrators build on cannot change by accident. The committed file <c>docs/api/openapi.v1.json</c> is the contract of the version 1; a change to
/// a route, a field or a code shows here as a difference, and a deliberate one is committed by running the tests with <c>SF_UPDATE_OPENAPI=1</c> and reading the diff of the file.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class OpenApiContractTests(ApiFixture api)
{
    private static string ContractPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SecureFact.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("The repository root was not found."), "docs", "api", "openapi.v1.json");
    }

    private async Task<JsonNode> PublishedAsync()
    {
        using var anonymous = api.NewClient();
        var response = await anonymous.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static string Canonical(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [Fact]
    public async Task The_description_of_the_api_is_public_and_says_how_to_authenticate()
    {
        var document = await PublishedAsync();

        Assert.Equal("SecureFact Perú API", document["info"]!["title"]!.GetValue<string>());
        Assert.Equal("v1", document["info"]!["version"]!.GetValue<string>());
        var bearer = document["components"]!["securitySchemes"]!["bearer"]!;
        Assert.Equal(("http", "bearer"), (bearer["type"]!.GetValue<string>(), bearer["scheme"]!.GetValue<string>()));
        var paths = document["paths"]!.AsObject().Select(p => p.Key).ToList();
        foreach (var expected in new[] { "/api/v1/documents", "/api/v1/api-keys", "/api/v1/companies", "/api/v1/gre/guides", "/api/v1/auth/login" })
        {
            Assert.Contains(expected, paths);
        }

        // Everything the platform publishes is under a version: the day there is a v2, v1 stays as it is.
        Assert.All(paths.Where(p => !p.StartsWith("/health", StringComparison.Ordinal)), path => Assert.StartsWith("/api/v1/", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_published_description_is_the_committed_contract()
    {
        var published = Canonical(await PublishedAsync());
        var path = ContractPath();
        if (Environment.GetEnvironmentVariable("SF_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, published + "\n");
        }

        var committed = Canonical(JsonNode.Parse(await File.ReadAllTextAsync(path))!);

        Assert.True(
            committed == published,
            "The API no longer matches docs/api/openapi.v1.json. If the change is deliberate, run the tests with SF_UPDATE_OPENAPI=1 and commit the file; if it is not, undo the change.");
    }
}
