using SecureFact.Billing.Contracts;

namespace SecureFact.Billing.Application;

/// <summary>Default when no module reports rejections or annulments: every document counts.</summary>
internal sealed class NoIneffectiveDocuments : IIneffectiveDocumentsProvider
{
    public Task<IReadOnlySet<Guid>> FindAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
}
