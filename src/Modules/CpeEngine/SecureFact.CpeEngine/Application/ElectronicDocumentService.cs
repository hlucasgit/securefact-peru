using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.Audit.Contracts;
using SecureFact.Billing.Contracts;
using SecureFact.Certificates.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.CpeEngine.Domain;
using SecureFact.CpeEngine.Infrastructure;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.Rules.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Application;

internal sealed class ElectronicDocumentService(
    CpeDbContext db,
    IDataScope scope,
    IDocumentService billing,
    ICompanyAdministration companies,
    IRuleProvider rules,
    ICertificateProvider certificates,
    ISolCredentialProvider solCredentials,
    IUblDocumentGenerator ubl,
    IXmlSigner signer,
    ICpePackager packager,
    ICdrParser cdrParser,
    IEDocumentStateMachine machine,
    IServiceProvider services,
    TimeProvider clock,
    IAuditTrail audit) : IElectronicDocumentService
{
    private const string OperationTypeSale = "0101";
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Error Missing = Error.NotFound(ErrorCodes.CpeNotFound, "Documento electrónico no encontrado", "El documento electrónico no existe o no es visible para este contexto.");

    public async Task<Result<ElectronicDocumentDto>> PrepareAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return NoTenant();
        }

        if (await db.ElectronicDocuments.AsNoTracking().SingleOrDefaultAsync(e => e.DocumentId == documentId, cancellationToken) is { } existing)
        {
            return ToDto(existing);
        }

        var document = await billing.GetAsync(documentId, cancellationToken);
        if (!document.IsSuccess)
        {
            return document.Error;
        }

        var d = document.Value;
        if (d.DocumentTypeCode is not (DocumentTypes.Invoice or DocumentTypes.Receipt))
        {
            return Error.Validation(ErrorCodes.CpeUnsupported, "Tipo de documento no soportado", "Solo facturas y boletas generan XML por ahora.");
        }

        var company = await companies.GetAsync(d.CompanyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        var igv = await rules.ResolveDecimalAsync(RuleCodes.IgvRate, "rate", d.IssueDate, cancellationToken);
        if (!igv.IsSuccess)
        {
            return igv.Error;
        }

        var data = new UblInvoiceData(
            d.DocumentTypeCode, d.Series, d.Number, d.IssueDate, null, d.Currency, OperationTypeSale,
            new UblParty(IdentityDocuments.Ruc, company.Value.Ruc, company.Value.LegalName, company.Value.TradeName),
            new UblParty(d.Buyer.DocumentTypeCode, d.Buyer.DocumentNumber, d.Buyer.Name),
            d.Lines.Select(l => new UblLine(l.LineNumber, l.Description, l.UnitCode, l.ProductCode, l.Quantity, l.UnitValue, null, l.IgvAffectationCode)).ToList(),
            d.Totals, igv.Value);

        var generated = ubl.GenerateInvoice(data);
        if (!generated.IsSuccess)
        {
            return generated.Error;
        }

        var certificate = await certificates.GetActiveSigningCertificateAsync(d.CompanyId, cancellationToken);
        if (!certificate.IsSuccess)
        {
            return certificate.Error;
        }

        Result<SignedDocument> signed;
        using (certificate.Value)
        {
            signed = signer.Sign(generated.Value.Xml, certificate.Value);
        }

        if (!signed.IsSuccess)
        {
            return signed.Error;
        }

        var packaged = packager.Zip(generated.Value.FileBaseName, signed.Value.Xml);
        if (!packaged.IsSuccess)
        {
            return packaged.Error;
        }

        var now = clock.GetUtcNow();
        var entity = ElectronicDocument.Create(
            Guid.CreateVersion7(), tenant.Value, d.Id, d.CompanyId, d.DocumentTypeCode, d.Series, d.Number,
            generated.Value.FileBaseName, signed.Value.Xml, signed.Value.DigestValue, now);
        var moved = Transition(entity, EDocumentEvent.DocumentSigned, "XML generado y firmado.", now);
        if (!moved.IsSuccess)
        {
            return moved.Error;
        }

        db.ElectronicDocuments.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request prepared the same document first: the unique index keeps one, return it.
            db.ChangeTracker.Clear();
            var winner = await db.ElectronicDocuments.AsNoTracking().SingleOrDefaultAsync(e => e.DocumentId == documentId, cancellationToken);
            return winner is null ? throw new InvalidOperationException("Electronic document insert failed.") : ToDto(winner);
        }

        await audit.RecordAsync(new AuditEvent(
            AuditActions.ElectronicDocumentPrepared, "electronic_document", entity.Id.ToString("D"), tenant.Value,
            NewValues: new Dictionary<string, object?> { ["documentId"] = d.Id, ["fileBaseName"] = entity.FileBaseName, ["digestValue"] = entity.DigestValue }), cancellationToken);
        return ToDto(entity);
    }

    public async Task<Result<ElectronicDocumentDto>> SendAsync(Guid electronicDocumentId, CancellationToken cancellationToken)
    {
        var entity = await db.ElectronicDocuments.SingleOrDefaultAsync(e => e.Id == electronicDocumentId, cancellationToken);
        if (entity is null)
        {
            return Missing;
        }

        if (entity.DocumentTypeCode != DocumentTypes.Invoice)
        {
            return Error.Validation(ErrorCodes.CpeUnsupported, "Envío no soportado", "Las boletas se informan en el resumen diario, que aún no está disponible.");
        }

        if (entity.Snapshot.IsTerminal)
        {
            return ToDto(entity);
        }

        if (entity.State == EDocumentState.Sending)
        {
            return Error.Conflict(ErrorCodes.CpeBusy, "Envío en curso", "El documento se está enviando a SUNAT.");
        }

        if (entity.State != EDocumentState.ReadyToSend)
        {
            return Error.Conflict(ErrorCodes.CpeInvalidTransition, "Transición de estado inválida", $"El documento está en estado {entity.State} y no se puede enviar. Si falló, reintente primero.");
        }

        if (services.GetService<ICpeSubmissionChannel>() is not { } channel)
        {
            return Error.Conflict(ErrorCodes.CpeChannelNotConfigured, "Canal SUNAT no configurado", "El envío a SUNAT no está configurado en este entorno.");
        }

        var company = await companies.GetAsync(entity.CompanyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        var secret = await solCredentials.GetAsync(entity.CompanyId, cancellationToken);
        if (!secret.IsSuccess)
        {
            return secret.Error;
        }

        var package = packager.Zip(entity.FileBaseName, entity.SignedXml);
        if (!package.IsSuccess)
        {
            return package.Error;
        }

        var started = clock.GetUtcNow();
        if (Transition(entity, EDocumentEvent.SendStarted, null, started) is { IsSuccess: false } refused)
        {
            return refused.Error;
        }

        entity.MarkSent(started);
        entity.ScheduleRetry(null);
        if (await TrySaveAsync(cancellationToken) is { } busy)
        {
            return busy;
        }

        var credentials = new SunatCredentials(company.Value.Ruc, secret.Value.SolUser, secret.Value.SolPassword);
        ChannelReply reply;
        try
        {
            reply = await channel.SendBillAsync(credentials, entity.FileBaseName + ".zip", package.Value, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up: the outcome at SUNAT is unknown, so the document goes back to the queue (never to a final state).
            Transition(entity, EDocumentEvent.TransientFailure, "Envío cancelado por el llamador.", clock.GetUtcNow());
            entity.ScheduleRetry(clock.GetUtcNow() + Backoff(entity.Attempts));
            await TrySaveAsync(CancellationToken.None);
            throw;
        }

        var outcome = await ApplyReplyAsync(entity, company.Value.Ruc, reply);
        if (await TrySaveAsync(CancellationToken.None) is { } conflict)
        {
            return conflict;
        }

        await RecordOutcomeAuditAsync(entity, outcome);
        return ToDto(entity);
    }

    public async Task<Result<ElectronicDocumentDto>> RetryAsync(Guid electronicDocumentId, CancellationToken cancellationToken)
    {
        var entity = await db.ElectronicDocuments.SingleOrDefaultAsync(e => e.Id == electronicDocumentId, cancellationToken);
        if (entity is null)
        {
            return Missing;
        }

        var moved = Transition(entity, EDocumentEvent.ManualRetry, "Reintento manual.", clock.GetUtcNow());
        if (!moved.IsSuccess)
        {
            return moved.Error;
        }

        entity.ScheduleRetry(null);
        if (await TrySaveAsync(cancellationToken) is { } busy)
        {
            return busy;
        }

        await audit.RecordAsync(new AuditEvent(
            AuditActions.ElectronicDocumentRetried, "electronic_document", entity.Id.ToString("D"), entity.TenantId,
            NewValues: new Dictionary<string, object?> { ["state"] = entity.State.ToString() }), cancellationToken);
        return ToDto(entity);
    }

    public async Task<Result<ElectronicDocumentDto>> GetAsync(Guid electronicDocumentId, CancellationToken cancellationToken)
    {
        var entity = await db.ElectronicDocuments.AsNoTracking().SingleOrDefaultAsync(e => e.Id == electronicDocumentId, cancellationToken);
        return entity is null ? Missing : ToDto(entity);
    }

    public async Task<Result<ElectronicDocumentDto>> GetByDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var entity = await db.ElectronicDocuments.AsNoTracking().SingleOrDefaultAsync(e => e.DocumentId == documentId, cancellationToken);
        return entity is null ? Missing : ToDto(entity);
    }

    public async Task<Result<IReadOnlyList<ElectronicDocumentEventDto>>> ListEventsAsync(Guid electronicDocumentId, CancellationToken cancellationToken)
    {
        if (!await db.ElectronicDocuments.AsNoTracking().AnyAsync(e => e.Id == electronicDocumentId, cancellationToken))
        {
            return Missing;
        }

        var rows = await db.Events.AsNoTracking().Where(e => e.ElectronicDocumentId == electronicDocumentId)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);
        return rows.Select(e => new ElectronicDocumentEventDto(e.Id, e.FromState, e.ToState, e.Event, e.Attempt, e.Detail, e.OccurredAt)).ToList();
    }

    public async Task<Result<string>> GetSignedXmlAsync(Guid electronicDocumentId, CancellationToken cancellationToken)
    {
        var xml = await db.ElectronicDocuments.AsNoTracking().Where(e => e.Id == electronicDocumentId).Select(e => e.SignedXml).SingleOrDefaultAsync(cancellationToken);
        return xml is null ? Missing : xml;
    }

    public async Task<Result<byte[]>> GetCdrZipAsync(Guid electronicDocumentId, CancellationToken cancellationToken)
    {
        var cdr = await db.ElectronicDocuments.AsNoTracking().Where(e => e.Id == electronicDocumentId).Select(e => e.CdrZip).SingleOrDefaultAsync(cancellationToken);
        return cdr is null ? Error.NotFound(ErrorCodes.CpeNotFound, "CDR no disponible", "SUNAT aún no ha devuelto un CDR para este documento.") : cdr;
    }

    // ---------- reply handling ----------

    private async Task<EDocumentState> ApplyReplyAsync(ElectronicDocument entity, string companyRuc, ChannelReply reply)
    {
        var now = clock.GetUtcNow();
        switch (reply.Outcome)
        {
            case ChannelOutcome.CdrReceived when reply.CdrZip is { } zip:
                return HandleCdr(entity, companyRuc, zip, now);

            case ChannelOutcome.Fault when reply.Fault is { } fault:
                entity.RecordError(fault.Code?.ToString(System.Globalization.CultureInfo.InvariantCulture), fault.Message);
                return fault.Retryable
                    ? Retry(entity, $"Falla reintentable de SUNAT ({fault.Side}).", now)
                    : Fail(entity, $"Falla de SUNAT que no se corrige reenviando ({fault.Side}).", now);

            case ChannelOutcome.Unreachable:
                entity.RecordError(null, reply.Fault?.Message);
                return Retry(entity, "SUNAT no respondió.", now);

            default:
                entity.RecordError(null, "Respuesta inesperada del canal de envío.");
                return Retry(entity, "Respuesta inesperada del canal.", now);
        }
    }

    private EDocumentState HandleCdr(ElectronicDocument entity, string companyRuc, byte[] zip, DateTimeOffset now)
    {
        var parsed = cdrParser.ParseZip(zip);
        if (!parsed.IsSuccess)
        {
            // SUNAT answered but we cannot read it: keep the bytes, never guess an outcome.
            entity.KeepRawCdr(zip);
            entity.RecordError(parsed.Error.Code, parsed.Error.Detail);
            return Fail(entity, "El CDR recibido no se pudo interpretar.", now);
        }

        var cdr = parsed.Value;
        if (cdr.ReferenceId != $"{entity.Series}-{entity.Number}" || cdr.TaxpayerRuc != companyRuc)
        {
            entity.KeepRawCdr(zip);
            entity.RecordError(ErrorCodes.CpeCdrMismatch, "El CDR no corresponde al documento enviado.");
            return Fail(entity, "El CDR recibido no corresponde al documento.", now);
        }

        entity.RecordCdr(zip, cdr, JsonSerializer.Serialize(cdr.Observations, Json), now);
        entity.RecordError(null, null);
        var @event = cdr.Status switch
        {
            CdrStatus.Accepted => EDocumentEvent.CdrAccepted,
            CdrStatus.AcceptedWithObservations => EDocumentEvent.CdrAcceptedWithObservations,
            _ => EDocumentEvent.CdrRejected,
        };
        Transition(entity, @event, $"CDR {cdr.ProcessId}: código {cdr.ResponseCode}.", now);
        return entity.State;
    }

    private EDocumentState Retry(ElectronicDocument entity, string detail, DateTimeOffset now)
    {
        Transition(entity, EDocumentEvent.TransientFailure, detail, now);
        entity.ScheduleRetry(entity.State == EDocumentState.ReadyToSend ? now + Backoff(entity.Attempts) : null);
        return entity.State;
    }

    private EDocumentState Fail(ElectronicDocument entity, string detail, DateTimeOffset now)
    {
        Transition(entity, EDocumentEvent.PermanentFailure, detail, now);
        entity.ScheduleRetry(null);
        return entity.State;
    }

    private static TimeSpan Backoff(int attempts)
    {
        var seconds = BaseBackoff.TotalSeconds * Math.Pow(2, Math.Max(attempts - 1, 0));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    private async Task RecordOutcomeAuditAsync(ElectronicDocument entity, EDocumentState state)
    {
        var action = state switch
        {
            EDocumentState.Accepted or EDocumentState.AcceptedWithObservations or EDocumentState.Rejected => AuditActions.ElectronicDocumentProcessed,
            EDocumentState.Failed => AuditActions.ElectronicDocumentFailed,
            _ => null,
        };
        if (action is null)
        {
            return;
        }

        await audit.RecordAsync(new AuditEvent(
            action, "electronic_document", entity.Id.ToString("D"), entity.TenantId,
            NewValues: new Dictionary<string, object?>
            {
                ["state"] = state.ToString(),
                ["attempts"] = entity.Attempts,
                ["cdrResponseCode"] = entity.CdrResponseCode,
                ["cdrProcessId"] = entity.CdrProcessId,
                ["errorCode"] = entity.LastErrorCode,
            }), CancellationToken.None);
    }

    // ---------- helpers ----------

    private Result<EDocumentSnapshot> Transition(ElectronicDocument entity, EDocumentEvent @event, string? detail, DateTimeOffset now)
    {
        var from = entity.Snapshot;
        var next = machine.Apply(from, @event);
        if (!next.IsSuccess)
        {
            return next;
        }

        entity.MoveTo(next.Value, now);
        db.Events.Add(ElectronicDocumentEvent.Create(entity.TenantId, entity.Id, from.State, next.Value.State, @event, next.Value.Attempts, detail, now));
        return next;
    }

    private async Task<Error?> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Error.Conflict(ErrorCodes.CpeBusy, "Operación concurrente", "Otra operación modificó el documento al mismo tiempo. Consulte su estado e intente de nuevo.");
        }
    }

    private static Error NoTenant() => Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");

    private static ElectronicDocumentDto ToDto(ElectronicDocument e) => new(
        e.Id, e.TenantId, e.DocumentId, e.CompanyId, e.DocumentTypeCode, e.Series, e.Number, e.FileBaseName, e.State, e.Attempts, e.DigestValue,
        e.Ticket, e.CdrProcessId, e.CdrResponseCode, e.CdrDescription,
        JsonSerializer.Deserialize<List<CdrObservation>>(e.CdrObservationsJson, Json) ?? [],
        e.LastErrorCode, e.LastErrorMessage, e.NextAttemptAt, e.CreatedAt, e.UpdatedAt, e.SentAt, e.ProcessedAt);
}
