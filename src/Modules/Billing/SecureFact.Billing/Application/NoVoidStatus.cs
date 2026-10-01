using SecureFact.Billing.Contracts;

namespace SecureFact.Billing.Application;

/// <summary>Default when no module reports annulments: nothing is voided.</summary>
internal sealed class NoVoidStatus : IVoidStatusProvider
{
    public Task<bool> IsVoidedOrBeingVoidedAsync(Guid documentId, CancellationToken cancellationToken) => Task.FromResult(false);
}
