using SecureFact.Rules.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class RuleEndpoints
{
    public static void MapRuleEndpoints(this IEndpointRouteBuilder app)
    {
        // Transparency: integrators can see which regulatory values (and how well verified) the platform applies on a given date.
        app.MapGet("/api/v1/rules", async (DateOnly? asOf, IRuleProvider rules, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await rules.ListAsync(asOf ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), ct)))
            .WithTags("Rules");
    }
}
