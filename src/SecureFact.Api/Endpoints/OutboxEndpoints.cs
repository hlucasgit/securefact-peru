using SecureFact.Audit.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications.Application;
using SecureFact.SharedKernel.Tenancy;
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

        // The queue of e-mails belongs to the platform (ADR-054): its dead e-mails are for platform staff, who may send one again.
        var emails = app.MapGroup("/api/v1/platform/emails").WithTags("Emails");

        emails.MapGet("/dead", async (IEmailDispatcher dispatcher, ICurrentUser user, CancellationToken ct) =>
            user.IsPlatform ? Results.Ok(await dispatcher.ListDeadAsync(100, ct)) : Results.Forbid()).RequireAuthorization(Permissions.TenantsRead);

        emails.MapPost("/{id:guid}/requeue", async (Guid id, IEmailDispatcher dispatcher, ICurrentUser user, IAuditTrail audit, CancellationToken ct) =>
        {
            if (!user.IsPlatform)
            {
                return Results.Forbid();
            }

            if (!await dispatcher.RequeueAsync(id, ct))
            {
                return Results.NotFound();
            }

            await audit.RecordAsync(new AuditEvent(AuditActions.EmailRequeued, "email", id.ToString("D"), null, ActorUserId: user.UserId), ct);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.TenantsManage);
    }
}
