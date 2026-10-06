using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SecureFact.CpeEngine.Contracts;
using SecureFact.CpeEngine.Domain;
using SecureFact.CpeEngine.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Messaging;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Storage;

namespace SecureFact.CpeEngine.Application;

/// <summary>
/// Keeps the signed XML and the CDR of an electronic document in object storage (ADR-005, ADR-036) and registers where they are and their SHA-256. The database copy stays the source the
/// platform works from; the archive is the durable, versioned copy that outlives it. Archiving is idempotent: a file already registered is left as it is, and a different content for a key
/// that already holds one fails visibly instead of replacing it.
/// </summary>
internal sealed class DocumentArchiver(CpeDbContext db, IObjectStorage storage, IDataScope scope, TimeProvider clock) : IDocumentArchive
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Error Missing = Error.NotFound(ErrorCodes.CpeNotFound, "Documento electrónico no encontrado", "El documento electrónico no existe o no es visible para este contexto.");

    /// <summary>The event for a document, written in the transaction of the change that caused it.</summary>
    public static OutboxMessageRow EventFor(ElectronicDocument document, string eventType, DateTimeOffset now) =>
        OutboxMessageRow.Create(
            document.TenantId, eventType,
            JsonSerializer.Serialize(new ElectronicDocumentEventPayload(document.Id, document.DocumentId, document.CompanyId, document.DocumentTypeCode, document.Series, document.Number), Json), now);

    /// <summary>Stores the file of <paramref name="kind"/> of the document and registers it. Nothing happens when it is registered already.</summary>
    public async Task ArchiveAsync(Guid electronicDocumentId, string kind, CancellationToken cancellationToken)
    {
        if (await db.ArchivedFiles.AnyAsync(f => f.ElectronicDocumentId == electronicDocumentId && f.Kind == kind, cancellationToken))
        {
            return;
        }

        var document = await db.ElectronicDocuments.AsNoTracking().SingleOrDefaultAsync(e => e.Id == electronicDocumentId, cancellationToken)
            ?? throw new InvalidOperationException("The electronic document to archive does not exist or is not visible.");

        var (content, contentType) = kind switch
        {
            ArchiveKinds.SignedXml => (Encoding.UTF8.GetBytes(document.SignedXml), "application/xml"),
            ArchiveKinds.CdrZip => (document.CdrZip, "application/zip"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of archived file."),
        };
        if (content is null || content.Length == 0)
        {
            // An event can come before the answer is recorded only by mistake: leave the message pending instead of completing it with nothing stored.
            throw new InvalidOperationException($"The electronic document has no {kind} to archive yet.");
        }

        var key = KeyFor(document, kind);
        var stored = await storage.PutAsync(key, content, contentType, cancellationToken);
        db.ArchivedFiles.Add(ArchivedFile.Create(document.TenantId, document.Id, kind, key, stored.VersionId, stored.Sha256, stored.Size, contentType, clock.GetUtcNow()));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent delivery registered the same file first: the unique index keeps one, which is what was wanted.
            db.ChangeTracker.Clear();
            if (!await db.ArchivedFiles.AnyAsync(f => f.ElectronicDocumentId == electronicDocumentId && f.Kind == kind, cancellationToken))
            {
                throw;
            }
        }
    }

    public async Task<Result<IReadOnlyList<ArchivedFileDto>>> ListAsync(Guid electronicDocumentId, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        if (!await db.ElectronicDocuments.AsNoTracking().AnyAsync(e => e.Id == electronicDocumentId, cancellationToken))
        {
            return Missing;
        }

        var files = await db.ArchivedFiles.AsNoTracking().Where(f => f.ElectronicDocumentId == electronicDocumentId).OrderBy(f => f.Kind).ToListAsync(cancellationToken);
        var list = new List<ArchivedFileDto>(files.Count);
        var expires = clock.GetUtcNow() + IDocumentArchive.LinkLifetime;
        foreach (var file in files)
        {
            var url = await storage.CreateDownloadUrlAsync(file.StorageKey, IDocumentArchive.LinkLifetime, cancellationToken);
            list.Add(new ArchivedFileDto(file.Kind, file.ContentType, file.SizeBytes, file.Sha256, file.StoredAt, url, expires));
        }

        return list;
    }

    /// <summary>
    /// ADR-005's key with the tenant first, so that a prefix is a tenant: <c>t/{tenant}/c/{company}/{year}/{document type}/{file base name}/{kind}/v1</c>. The file base name (RUC, type,
    /// series and number, or the name of the summary) is unique per tenant, which a series and number are not for summaries and voids (their number repeats every day).
    /// </summary>
    internal static string KeyFor(ElectronicDocument document, string kind) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"t/{document.TenantId:N}/c/{document.CompanyId:N}/{document.IssueDate.Year:D4}/{document.DocumentTypeCode}/{document.FileBaseName}/{kind}/v1");
}

/// <summary>Consumer of the events of the CPE engine that archives the file of a kind (ADR-036). Idempotent: delivery is at least once.</summary>
internal sealed class ArchiveConsumer(string eventType, string kind, DocumentArchiver archiver) : IIntegrationEventConsumer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string EventType => eventType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ElectronicDocumentEventPayload>(message.PayloadJson, Json)
            ?? throw new InvalidOperationException("The event has no payload.");
        await archiver.ArchiveAsync(payload.ElectronicDocumentId, kind, cancellationToken);
    }
}
