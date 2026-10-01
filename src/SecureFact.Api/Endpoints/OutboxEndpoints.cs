using SecureFact.Identity.Contracts;
using SecureFact.SharedKernel.Messaging;

namespace SecureFact.Api.Endpoints;

internal static class OutboxEndpoints
{
    public static void MapOutboxEndpoints(this IEndpointRouteBuilder app)
    {
        var outbox = app.MapGroup("/api/v1/outbox").WithTags("Outbox");

        // Messages that exhausted their attempts, for the current tenant (Row Level Security applies).
        outbox.MapGet("/dead", async (IEnumerable<IOutboxSource> sources, CancellationToken ct) =>
        {
            var dead = new List<DeadOutboxMessage>();
            foreach (var source in sources)
            {
                dead.AddRange(await source.ListDeadAsync(100, ct));
            }

            return Results.Ok(dead.OrderBy(d => d.CreatedAt));
        }).RequireAuthorization(Permissions.CpeSend);

        outbox.MapPost("/{source}/{id:guid}/requeue", async (string source, Guid id, IEnumerable<IOutboxSource> sources, CancellationToken ct) =>
            sources.FirstOrDefault(s => s.Name == source) is { } found && await found.RequeueAsync(id, ct)
                ? Results.NoContent()
                : Results.NotFound()).RequireAuthorization(Permissions.CpeSend);
    }
}
