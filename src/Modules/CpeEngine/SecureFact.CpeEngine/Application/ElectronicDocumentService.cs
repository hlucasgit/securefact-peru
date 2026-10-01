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
    IQrPayloadGenerator qr,
    IPrintedRepresentationRenderer printer,
    IEDocumentStateMachine machine,
    IServiceProvider services,
    TimeProvider clock,
    IAuditTrail audit) : IElectronicDocumentService
{
    private const string OperationTypeSale = "0101";
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan PreconditionBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TicketPollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(24);
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
        if (d.DocumentTypeCode is not (DocumentTypes.Invoice or DocumentTypes.Receipt or DocumentTypes.CreditNote or DocumentTypes.DebitNote))
        {
            return Error.Validation(ErrorCodes.CpeUnsupported, "Tipo de documento no soportado", "Solo facturas, boletas y notas de crédito o débito generan XML.");
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

        var issuer = new UblParty(IdentityDocuments.Ruc, company.Value.Ruc, company.Value.LegalName, company.Value.TradeName);
        var buyer = new UblParty(d.Buyer.DocumentTypeCode, d.Buyer.DocumentNumber, d.Buyer.Name);
        var ublLines = d.Lines.Select(l => new UblLine(
            l.LineNumber, l.Description, l.UnitCode, l.ProductCode, l.Quantity, l.UnitValue, null, l.IgvAffectationCode,
            l.DiscountAffectingBase, l.ChargeAffectingBase, l.DiscountNotAffectingBase, l.ChargeNotAffectingBase)).ToList();

        var generated = d.Note is { } note
            ? ubl.GenerateNote(new UblNoteData(
                d.DocumentTypeCode, d.Series, d.Number, d.IssueDate, null, d.Currency, note.ReasonCode, note.Reason,
                note.ReferencedDocumentTypeCode, note.ReferencedSeries, note.ReferencedNumber, issuer, buyer, ublLines, d.Totals, igv.Value))
            : ubl.GenerateInvoice(new UblInvoiceData(
                d.DocumentTypeCode, d.Series, d.Number, d.IssueDate, null, d.Currency, OperationTypeSale, issuer, buyer, ublLines, d.Totals, igv.Value, Adjustments: d.Adjustments));
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
            generated.Value.FileBaseName, signed.Value.Xml, signed.Value.DigestValue, now, d.IssueDate, d.Note?.ReferencedDocumentId, d.Note?.ReferencedDocumentTypeCode);
        var moved = Lifecycle.Transition(db, machine, entity, EDocumentEvent.DocumentSigned, "XML generado y firmado.", now);
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

        var isNote = entity.DocumentTypeCode is DocumentTypes.CreditNote or DocumentTypes.DebitNote;
        if (entity.DocumentTypeCode is not (DocumentTypes.Invoice or ElectronicDocument.SummaryType or ElectronicDocument.VoidType) && !(isNote && entity.ReferenceTypeCode == DocumentTypes.Invoice))
        {
            return Error.Validation(ErrorCodes.CpeUnsupported, "Envío no soportado", "Las boletas y sus notas se informan en el resumen diario: cree el resumen y envíelo.");
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

        if (isNote && await CheckReferencedAcceptedAsync(entity, cancellationToken) is { } notAccepted)
        {
            return await DeferAsync(entity, notAccepted, cancellationToken);
        }

        var prepared = await PrepareRemoteCallAsync(entity, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return await DeferAsync(entity, prepared.Error, cancellationToken);
        }

        var (channel, credentials) = prepared.Value;
        var package = packager.Zip(entity.FileBaseName, entity.SignedXml);
        if (!package.IsSuccess)
        {
            return package.Error;
        }

        var children = await LoadChildrenAsync(entity, cancellationToken);
        if (entity.IsSummary && children.Any(c => c.Item.LineStatus == 1 && c.Document.State != EDocumentState.ReadyToSend))
        {
            return Error.Conflict(ErrorCodes.CpeInvalidTransition, "Transición de estado inválida", "Alguna boleta del resumen ya no está lista para enviarse.");
        }

        var started = clock.GetUtcNow();
        if (Lifecycle.Transition(db, machine, entity, EDocumentEvent.SendStarted, null, started) is { IsSuccess: false } refused)
        {
            return refused.Error;
        }

        Propagate(entity, children, EDocumentEvent.SendStarted, "Resumen enviado a SUNAT.", started, null);
        entity.MarkSent(started);
        entity.ScheduleRetry(null);
        if (await TrySaveAsync(cancellationToken) is { } busy)
        {
            return busy;
        }

        ChannelReply reply;
        try
        {
            reply = entity.IsTicketBatch
                ? await channel.SendSummaryAsync(credentials, entity.FileBaseName + ".zip", package.Value, cancellationToken)
                : await channel.SendBillAsync(credentials, entity.FileBaseName + ".zip", package.Value, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up: the outcome at SUNAT is unknown, so the document goes back to the queue (never to a final state).
            var at = clock.GetUtcNow();
            Lifecycle.Transition(db, machine, entity, EDocumentEvent.TransientFailure, "Envío cancelado por el llamador.", at);
            Propagate(entity, children, EDocumentEvent.TransientFailure, "Envío cancelado por el llamador.", at, null);
            entity.ScheduleRetry(at + Backoff(entity.Attempts));
            await TrySaveAsync(CancellationToken.None);
            throw;
        }

        var outcome = ApplyReply(entity, children, credentials.Ruc, reply);
        if (await TrySaveAsync(CancellationToken.None) is { } conflict)
        {
            return conflict;
        }

        await RecordOutcomeAuditAsync(entity, outcome);
        return ToDto(entity);
    }

    public async Task<Result<ElectronicDocumentDto>> PollAsync(Guid electronicDocumentId, CancellationToken cancellationToken)
    {
        var entity = await db.ElectronicDocuments.SingleOrDefaultAsync(e => e.Id == electronicDocumentId, cancellationToken);
        if (entity is null)
        {
            return Missing;
        }

        if (entity.Snapshot.IsTerminal)
        {
            return ToDto(entity);
        }

        if (entity.State != EDocumentState.AwaitingTicket || string.IsNullOrEmpty(entity.Ticket))
        {
            return Error.Conflict(ErrorCodes.CpeInvalidTransition, "Transición de estado inválida", $"El documento está en estado {entity.State} y no tiene un ticket pendiente.");
        }

        var prepared = await PrepareRemoteCallAsync(entity, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return await DeferAsync(entity, prepared.Error, cancellationToken);
        }

        var (channel, credentials) = prepared.Value;
        var children = await LoadChildrenAsync(entity, cancellationToken);
        var reply = await channel.GetStatusAsync(credentials, entity.Ticket, cancellationToken);
        var now = clock.GetUtcNow();

        EDocumentState outcome;
        if (reply.Outcome == ChannelOutcome.InProgress)
        {
            outcome = StillWaiting(entity, "SUNAT aún procesa el ticket.", now);
        }
        else if (reply.Outcome == ChannelOutcome.CdrReceived && reply.CdrZip is { } zip)
        {
            outcome = HandleCdr(entity, children, credentials.Ruc, zip, now);
        }
        else if (reply.Outcome == ChannelOutcome.Fault && reply.Fault is { Retryable: false } permanent)
        {
            entity.RecordError(permanent.Code?.ToString(System.Globalization.CultureInfo.InvariantCulture), permanent.Message);
            outcome = Fail(entity, children, "Falla de SUNAT al consultar el ticket que no se corrige reintentando.", now);
        }
        else
        {
            entity.RecordError(reply.Fault?.Code?.ToString(System.Globalization.CultureInfo.InvariantCulture), reply.Fault?.Message ?? "Respuesta inesperada del canal.");
            outcome = StillWaiting(entity, "No se pudo consultar el ticket; se reintentará.", now);
        }

        if (outcome == EDocumentState.AwaitingTicket && entity.SentAt is { } sent && now - sent > TicketLifetime)
        {
            outcome = Fail(entity, children, "SUNAT no respondió al ticket dentro de 24 horas.", now);
        }

        if (await TrySaveAsync(CancellationToken.None) is { } conflict)
        {
            return conflict;
        }

        await RecordOutcomeAuditAsync(entity, outcome);
        return ToDto(entity);
    }

    public async Task<Result<ElectronicDocumentDto>> RecoverAsync(Guid electronicDocumentId, CancellationToken cancellationToken)
    {
        var entity = await db.ElectronicDocuments.SingleOrDefaultAsync(e => e.Id == electronicDocumentId, cancellationToken);
        if (entity is null)
        {
            return Missing;
        }

        var now = clock.GetUtcNow();
        if (entity.State != EDocumentState.Sending)
        {
            return Error.Conflict(ErrorCodes.CpeInvalidTransition, "Transición de estado inválida", $"El documento está en estado {entity.State}; solo un envío atascado se puede recuperar.");
        }

        if (now - entity.UpdatedAt < ICpeWorkProcessor.SendingLease)
        {
            return Error.Conflict(ErrorCodes.CpeBusy, "Envío en curso", "El documento todavía está dentro del plazo de envío; espere antes de recuperarlo.");
        }

        var moved = Lifecycle.Transition(db, machine, entity, EDocumentEvent.Recovered, "Recuperado por un operador tras un envío sin respuesta.", now);
        if (!moved.IsSuccess)
        {
            return moved.Error;
        }

        Propagate(entity, await LoadChildrenAsync(entity, cancellationToken), EDocumentEvent.Recovered, "Resumen recuperado por un operador.", now, null);
        entity.ScheduleRetry(null);
        if (await TrySaveAsync(cancellationToken) is { } busy)
        {
            return busy;
        }

        await audit.RecordAsync(new AuditEvent(
            AuditActions.ElectronicDocumentRecovered, "electronic_document", entity.Id.ToString("D"), entity.TenantId,
            NewValues: new Dictionary<string, object?> { ["state"] = entity.State.ToString(), ["attempts"] = entity.Attempts }), cancellationToken);
        return ToDto(entity);
    }

    public async Task<Result<ElectronicDocumentDto>> RetryAsync(Guid electronicDocumentId, CancellationToken cancellationToken)
    {
        var entity = await db.ElectronicDocuments.SingleOrDefaultAsync(e => e.Id == electronicDocumentId, cancellationToken);
        if (entity is null)
        {
            return Missing;
        }

        var now = clock.GetUtcNow();
        var moved = Lifecycle.Transition(db, machine, entity, EDocumentEvent.ManualRetry, "Reintento manual.", now);
        if (!moved.IsSuccess)
        {
            return moved.Error;
        }

        Propagate(entity, await LoadChildrenAsync(entity, cancellationToken), EDocumentEvent.ManualRetry, "Reintento manual del resumen.", now, null);
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
        return entity is null ? Missing : ToDto(entity) with { Voided = await IsVoidedAsync(entity.Id, cancellationToken) };
    }

    public async Task<Result<ElectronicDocumentDto>> GetByDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var entity = await db.ElectronicDocuments.AsNoTracking().SingleOrDefaultAsync(e => e.DocumentId == documentId, cancellationToken);
        return entity is null ? Missing : ToDto(entity) with { Voided = await IsVoidedAsync(entity.Id, cancellationToken) };
    }

    /// <summary>A document is voided once a voided-documents communication that covers it was accepted by SUNAT.</summary>
    private Task<bool> IsVoidedAsync(Guid electronicDocumentId, CancellationToken cancellationToken) =>
        db.SummaryItems.AsNoTracking()
            .Where(i => i.ElectronicDocumentId == electronicDocumentId && i.ReleasedAt == null && i.LineStatus == 3)
            .Join(
                db.ElectronicDocuments.AsNoTracking().Where(e => (e.DocumentTypeCode == ElectronicDocument.VoidType || e.DocumentTypeCode == ElectronicDocument.SummaryType) && e.State == EDocumentState.Accepted),
                i => i.SummaryId, e => e.Id, (i, e) => i)
            .AnyAsync(cancellationToken);

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

    public async Task<Result<byte[]>> GetPdfAsync(Guid electronicDocumentId, CancellationToken cancellationToken)
    {
        var entity = await db.ElectronicDocuments.AsNoTracking().SingleOrDefaultAsync(e => e.Id == electronicDocumentId, cancellationToken);
        if (entity is null)
        {
            return Missing;
        }

        if (entity.DocumentTypeCode is not (DocumentTypes.Invoice or DocumentTypes.Receipt or DocumentTypes.CreditNote or DocumentTypes.DebitNote))
        {
            return Error.Validation(ErrorCodes.CpeUnsupported, "Sin representación impresa", "Solo las facturas, boletas y notas tienen representación impresa.");
        }

        var document = await billing.GetAsync(entity.DocumentId, cancellationToken);
        if (!document.IsSuccess)
        {
            return document.Error;
        }

        var company = await companies.GetAsync(entity.CompanyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        var d = document.Value;
        var identified = d.Buyer.DocumentTypeCode != IdentityDocuments.NoDocument;
        var payload = qr.Build(new QrData(
            company.Value.Ruc, d.DocumentTypeCode, d.Series, d.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Totals.TotalIgv, d.Totals.PayableAmount,
            d.IssueDate, identified ? d.Buyer.DocumentTypeCode : null, identified ? d.Buyer.DocumentNumber : null, entity.DigestValue));
        if (!payload.IsSuccess)
        {
            return payload.Error;
        }

        var printed = new PrintedDocument(
            d.DocumentTypeCode, d.Series, d.Number, d.IssueDate, d.Currency, company.Value.LegalName, company.Value.TradeName, company.Value.Ruc, company.Value.FiscalAddress,
            identified ? BuyerTypeName(d.Buyer.DocumentTypeCode) : null, identified ? d.Buyer.DocumentNumber : null, identified ? d.Buyer.Name : null, identified ? d.Buyer.Address : null,
            d.Lines.Select(l => new PrintedLine(l.UnitCode, l.Quantity, l.Description, l.UnitValue, l.UnitPriceIncludingTaxes, l.LineExtensionAmount, l.TotalTaxAmount)).ToList(),
            new PrintedTotals(d.Totals.TotalTaxableGravado, d.Totals.TotalExempt, d.Totals.TotalUnaffected, d.Totals.TotalFree, d.Totals.TotalIgv, d.Totals.PayableAmount, d.Totals.TotalAllowances, d.Totals.TotalCharges),
            payload.Value, entity.DigestValue,
            d.Note is { } note ? new PrintedNote($"{DocumentName(note.ReferencedDocumentTypeCode)} {note.ReferencedSeries}-{note.ReferencedNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}", note.Reason) : null,
            await IsVoidedAsync(entity.Id, cancellationToken));
        return printer.Render(printed);
    }

    private static string DocumentName(string typeCode) => typeCode == DocumentTypes.Invoice ? "Factura electrónica" : "Boleta de venta electrónica";

    /// <summary>The printed form replaces the catalogue 06 code by its denomination (annex rule for field "tipo y número de documento del adquirente").</summary>
    internal static string BuyerTypeName(string code) => code switch
    {
        IdentityDocuments.Ruc => "RUC",
        IdentityDocuments.Dni => "DNI",
        IdentityDocuments.ForeignerCard => "Carné de extranjería",
        IdentityDocuments.Passport => "Pasaporte",
        IdentityDocuments.DiplomaticId => "Cédula diplomática",
        _ => $"Documento {code}",
    };

    public async Task<Result<byte[]>> GetCdrZipAsync(Guid electronicDocumentId, CancellationToken cancellationToken)
    {
        var cdr = await db.ElectronicDocuments.AsNoTracking().Where(e => e.Id == electronicDocumentId).Select(e => e.CdrZip).SingleOrDefaultAsync(cancellationToken);
        return cdr is null ? Error.NotFound(ErrorCodes.CpeNotFound, "CDR no disponible", "SUNAT aún no ha devuelto un CDR para este documento.") : cdr;
    }

    // ---------- remote call preparation ----------

    /// <summary>SUNAT rejects a note whose document is not registered (error 2119), so a note waits until the document it modifies is accepted.</summary>
    private async Task<Error?> CheckReferencedAcceptedAsync(ElectronicDocument note, CancellationToken cancellationToken)
    {
        var state = await db.ElectronicDocuments.AsNoTracking()
            .Where(e => e.DocumentId == note.ReferenceDocumentId)
            .Select(e => (EDocumentState?)e.State)
            .SingleOrDefaultAsync(cancellationToken);
        return state switch
        {
            EDocumentState.Accepted or EDocumentState.AcceptedWithObservations => null,
            null => Error.Conflict(ErrorCodes.CpeReferenceNotAccepted, "Documento referenciado sin preparar", "El documento que modifica la nota aún no tiene documento electrónico."),
            _ => Error.Conflict(ErrorCodes.CpeReferenceNotAccepted, "Documento referenciado no aceptado", $"El documento que modifica la nota está en estado {state} y debe estar aceptado por SUNAT antes de enviar la nota."),
        };
    }

    /// <summary>
    /// A precondition (SOL credentials, channel, company) is missing. The document keeps its state and attempts, but its next attempt moves
    /// away so that a handful of misconfigured documents can never fill the worker's batch and starve everyone else.
    /// </summary>
    private async Task<Error> DeferAsync(ElectronicDocument entity, Error error, CancellationToken cancellationToken)
    {
        entity.RecordError(error.Code, error.Detail);
        entity.ScheduleRetry(clock.GetUtcNow() + PreconditionBackoff);
        await TrySaveAsync(cancellationToken);
        return error;
    }

    private async Task<Result<(ICpeSubmissionChannel Channel, SunatCredentials Credentials)>> PrepareRemoteCallAsync(ElectronicDocument entity, CancellationToken cancellationToken)
    {
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

        return (channel, new SunatCredentials(company.Value.Ruc, secret.Value.SolUser, secret.Value.SolPassword));
    }

    // ---------- reply handling ----------

    private EDocumentState ApplyReply(ElectronicDocument entity, IReadOnlyList<Child> children, string companyRuc, ChannelReply reply)
    {
        var now = clock.GetUtcNow();
        switch (reply.Outcome)
        {
            case ChannelOutcome.CdrReceived when reply.CdrZip is { } zip && !entity.IsTicketBatch:
                return HandleCdr(entity, children, companyRuc, zip, now);

            case ChannelOutcome.TicketIssued when entity.IsTicketBatch && !string.IsNullOrWhiteSpace(reply.Ticket):
                entity.RecordTicket(reply.Ticket);
                Lifecycle.Transition(db, machine, entity, EDocumentEvent.TicketIssued, $"Ticket {reply.Ticket}.", now);
                Propagate(entity, children, EDocumentEvent.TicketIssued, $"Resumen recibido por SUNAT (ticket {reply.Ticket}).", now, null);
                entity.ScheduleRetry(now + TicketPollInterval);
                return entity.State;

            case ChannelOutcome.Fault when reply.Fault is { } fault:
                entity.RecordError(fault.Code?.ToString(System.Globalization.CultureInfo.InvariantCulture), fault.Message);
                return fault.Retryable
                    ? Retry(entity, children, $"Falla reintentable de SUNAT ({fault.Side}).", now)
                    : Fail(entity, children, $"Falla de SUNAT que no se corrige reenviando ({fault.Side}).", now);

            case ChannelOutcome.Unreachable:
                entity.RecordError(null, reply.Fault?.Message);
                return Retry(entity, children, "SUNAT no respondió.", now);

            default:
                entity.RecordError(null, "Respuesta inesperada del canal de envío.");
                return Retry(entity, children, "Respuesta inesperada del canal.", now);
        }
    }

    private EDocumentState HandleCdr(ElectronicDocument entity, IReadOnlyList<Child> children, string companyRuc, byte[] zip, DateTimeOffset now)
    {
        var parsed = cdrParser.ParseZip(zip);
        if (!parsed.IsSuccess)
        {
            // SUNAT answered but we cannot read it: keep the bytes, never guess an outcome.
            entity.KeepRawCdr(zip);
            entity.RecordError(parsed.Error.Code, parsed.Error.Detail);
            return Fail(entity, children, "El CDR recibido no se pudo interpretar.", now);
        }

        var cdr = parsed.Value;
        var expectedReference = entity.IsTicketBatch ? entity.FileBaseName[(companyRuc.Length + 1)..] : $"{entity.Series}-{entity.Number}";
        if (cdr.ReferenceId != expectedReference || cdr.TaxpayerRuc != companyRuc)
        {
            entity.KeepRawCdr(zip);
            entity.RecordError(ErrorCodes.CpeCdrMismatch, "El CDR no corresponde al documento enviado.");
            return Fail(entity, children, "El CDR recibido no corresponde al documento.", now);
        }

        var observations = JsonSerializer.Serialize(cdr.Observations, Json);
        entity.RecordCdr(zip, cdr, observations, now);
        entity.RecordError(null, null);
        entity.ScheduleRetry(null);
        var @event = cdr.Status switch
        {
            CdrStatus.Accepted => EDocumentEvent.CdrAccepted,
            CdrStatus.AcceptedWithObservations => EDocumentEvent.CdrAcceptedWithObservations,
            _ => EDocumentEvent.CdrRejected,
        };
        var detail = $"CDR {cdr.ProcessId}: código {cdr.ResponseCode}.";
        Lifecycle.Transition(db, machine, entity, @event, detail, now);
        Propagate(entity, children, @event, detail, now, (cdr, observations));
        return entity.State;
    }

    private EDocumentState Retry(ElectronicDocument entity, IReadOnlyList<Child> children, string detail, DateTimeOffset now)
    {
        Lifecycle.Transition(db, machine, entity, EDocumentEvent.TransientFailure, detail, now);
        Propagate(entity, children, EDocumentEvent.TransientFailure, detail, now, null);
        entity.ScheduleRetry(entity.State == EDocumentState.ReadyToSend ? now + Backoff(entity.Attempts) : null);
        return entity.State;
    }

    private EDocumentState StillWaiting(ElectronicDocument entity, string detail, DateTimeOffset now)
    {
        Lifecycle.Transition(db, machine, entity, EDocumentEvent.StillProcessing, detail, now);
        entity.ScheduleRetry(now + TicketPollInterval);
        return entity.State;
    }

    private EDocumentState Fail(ElectronicDocument entity, IReadOnlyList<Child> children, string detail, DateTimeOffset now)
    {
        Lifecycle.Transition(db, machine, entity, EDocumentEvent.PermanentFailure, detail, now);
        Propagate(entity, children, EDocumentEvent.PermanentFailure, detail, now, null);
        entity.ScheduleRetry(null);
        return entity.State;
    }

    private static TimeSpan Backoff(int attempts)
    {
        var seconds = BaseBackoff.TotalSeconds * Math.Pow(2, Math.Max(attempts - 1, 0));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    // ---------- summaries: the receipts follow the summary ----------

    private sealed record Child(SummaryItem Item, ElectronicDocument Document);

    private async Task<IReadOnlyList<Child>> LoadChildrenAsync(ElectronicDocument summary, CancellationToken cancellationToken)
    {
        if (!summary.IsTicketBatch)
        {
            return [];
        }

        var items = await db.SummaryItems.Where(i => i.SummaryId == summary.Id && i.ReleasedAt == null).OrderBy(i => i.LineNumber).ToListAsync(cancellationToken);
        var ids = items.Select(i => i.ElectronicDocumentId).ToList();
        var documents = await db.ElectronicDocuments.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        return items.Select(i => new Child(i, documents[i.ElectronicDocumentId])).ToList();
    }

    /// <summary>
    /// Receipts have no CDR of their own: each one mirrors its summary. A rejected summary does not judge the receipts, so they return to the
    /// queue and their items are released to be reported again.
    /// </summary>
    private void Propagate(ElectronicDocument summary, IReadOnlyList<Child> children, EDocumentEvent @event, string detail, DateTimeOffset now, (CdrInfo Cdr, string ObservationsJson)? outcome)
    {
        if (!summary.IsTicketBatch)
        {
            return;
        }

        // Items that void a document (an "RA" communication, or a summary line with status 3) concern documents that already have a final
        // answer: they never change state. A rejected file frees them to be voided again.
        if (@event == EDocumentEvent.CdrRejected)
        {
            foreach (var voided in children.Where(c => c.Item.LineStatus == 3))
            {
                voided.Item.Release(now);
            }
        }

        children = children.Where(c => c.Item.LineStatus == 1).ToList();
        if (summary.IsVoidCommunication)
        {
            return;
        }

        EDocumentEvent? mapped = @event switch
        {
            EDocumentEvent.SendStarted => EDocumentEvent.SendStarted,
            EDocumentEvent.TicketIssued => EDocumentEvent.TicketIssued,
            EDocumentEvent.CdrAccepted => EDocumentEvent.CdrAccepted,
            EDocumentEvent.CdrAcceptedWithObservations => EDocumentEvent.CdrAcceptedWithObservations,
            EDocumentEvent.CdrRejected => EDocumentEvent.ReturnedToQueue,
            EDocumentEvent.TransientFailure => EDocumentEvent.TransientFailure,
            EDocumentEvent.PermanentFailure => EDocumentEvent.PermanentFailure,
            EDocumentEvent.ManualRetry => EDocumentEvent.ManualRetry,
            EDocumentEvent.Recovered => EDocumentEvent.Recovered,
            _ => null,
        };
        if (mapped is null)
        {
            return;
        }

        foreach (var child in children)
        {
            // Best effort: a receipt that is no longer in lockstep (for example already final) simply stays as it is.
            if (!Lifecycle.Transition(db, machine, child.Document, mapped.Value, $"Resumen {summary.FileBaseName}: {detail}", now).IsSuccess)
            {
                continue;
            }

            if (outcome is { } o && mapped is EDocumentEvent.CdrAccepted or EDocumentEvent.CdrAcceptedWithObservations)
            {
                child.Document.RecordSummaryOutcome(o.Cdr, o.ObservationsJson, now);
            }

            if (mapped == EDocumentEvent.ReturnedToQueue)
            {
                child.Item.Release(now);
            }
        }
    }

    // ---------- helpers ----------

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

    internal static ElectronicDocumentDto ToDto(ElectronicDocument e) => new(
        e.Id, e.TenantId, e.DocumentId, e.CompanyId, e.DocumentTypeCode, e.Series, e.Number, e.FileBaseName, e.State, e.Attempts, e.DigestValue,
        e.Ticket, e.CdrProcessId, e.CdrResponseCode, e.CdrDescription,
        JsonSerializer.Deserialize<List<CdrObservation>>(e.CdrObservationsJson, Json) ?? [],
        e.LastErrorCode, e.LastErrorMessage, e.NextAttemptAt, e.CreatedAt, e.UpdatedAt, e.SentAt, e.ProcessedAt, e.IssueDate);
}

/// <summary>Applies an event through the state machine and records it in the history, on the unit of work of the caller.</summary>
internal static class Lifecycle
{
    public static Result<EDocumentSnapshot> Transition(
        CpeDbContext db, IEDocumentStateMachine machine, ElectronicDocument entity, EDocumentEvent @event, string? detail, DateTimeOffset now)
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
}
