using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Catalogs.Contracts;
using SecureFact.Certificates.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Gre.Contracts;
using SecureFact.Gre.Domain;
using SecureFact.Gre.Infrastructure;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.Rules.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Gre.Application;

/// <summary>
/// The life of a guide of the sender (ADR-056): validate, number, build, sign and store it; send it to SUNAT; ask for the answer; keep the CDR. Nothing here decides a tax rule: the shape
/// rules are in <see cref="GreValidator"/>, the parameters are rules as data, and what only SUNAT knows (the registers) SUNAT answers.
/// </summary>
internal sealed class GreService(
    GreDbContext db,
    IDataScope scope,
    ICompanyAdministration companies,
    ICatalogReader catalogs,
    IRuleProvider rules,
    ICertificateProvider certificates,
    ISolCredentialProvider solCredentials,
    IXmlSigner signer,
    ICpePackager packager,
    ICdrParser cdrParser,
    IGreChannel channel,
    TimeProvider clock,
    IAuditTrail audit) : IGreService
{
    private const int MaxPage = 100;
    private const string CompanyType = "09";

    private static readonly TimeZoneInfo Lima = TimeZoneInfo.FindSystemTimeZoneById("America/Lima");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Error Missing = Error.NotFound(ErrorCodes.GuideNotFound, "Guía no encontrada", "La guía no existe o no es visible para este contexto.");

    private static readonly HashSet<string> NotYetSupportedMotives = new(["08", "09", "18", "19"], StringComparer.Ordinal);

    public async Task<Result<GreDto>> CreateAsync(CreateGreRequest request, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        if (request is null || request.Goods is null || request.Recipient is null || request.Origin is null || request.Destination is null)
        {
            return Error.Validation(ErrorCodes.InvalidGuide, "Guía inválida", "Faltan datos obligatorios: destinatario, puntos de partida y llegada, o bienes.");
        }

        if (NotYetSupportedMotives.Contains(request.MotiveCode ?? string.Empty))
        {
            return Error.Validation(
                ErrorCodes.GuideNotSupported, "Motivo aún no disponible",
                "Importación (08), exportación (09), traslado de mercancía extranjera (19) y emisor itinerante (18) aún no se emiten desde aquí: emítalos en SUNAT Operaciones en Línea.");
        }

        var company = await companies.GetAsync(request.CompanyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        var series = await db.Series.AsNoTracking().SingleOrDefaultAsync(s => s.Id == request.SeriesId && s.CompanyId == request.CompanyId, cancellationToken);
        if (series is null || !series.IsActive)
        {
            return Error.NotFound(ErrorCodes.GreSeriesNotFound, "Serie no encontrada", "La serie no existe, está desactivada o no es de esa empresa.");
        }

        var now = clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, Lima);
        var today = DateOnly.FromDateTime(local.DateTime);
        var context = await ContextAsync(company.Value.Ruc, today, cancellationToken);
        if (!context.IsSuccess)
        {
            return context.Error;
        }

        var issues = GreValidator.Validate(request, context.Value);
        if (issues.Count > 0)
        {
            return Error.Validation(ErrorCodes.InvalidGuide, "Guía inválida", string.Join(" ", issues.Select(i => $"• {i}")));
        }

        var issueDate = request.IssueDate ?? today;
        var certificate = await certificates.GetActiveSigningCertificateAsync(company.Value.Id, cancellationToken);
        if (!certificate.IsSuccess)
        {
            return certificate.Error;
        }

        using var signingCertificate = certificate.Value;
        var names = (await catalogs.GetEntriesAsync("61", today, cancellationToken)) is { IsSuccess: true } entries
            ? entries.Value.ToDictionary(e => e.Code, e => e.Description, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var numbers = await db.Database.SqlQuery<long>($"""
            UPDATE gre.series SET last_number = last_number + 1, updated_at = {now}
            WHERE id = {series.Id} AND is_active AND last_number < {GreSeries.MaxNumber}
            RETURNING last_number AS "Value"
            """).ToListAsync(cancellationToken);
        if (numbers.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Error.Validation(ErrorCodes.GreSeriesExhausted, "Serie agotada o inactiva", "La serie no puede emitir más números.");
        }

        var number = numbers[0];
        var xml = GreUblGenerator.Generate(new GreXmlData(company.Value.Ruc, company.Value.LegalName, series.Code, number, issueDate, TimeOnly.FromDateTime(local.DateTime), request, names));
        var signed = signer.Sign(xml, signingCertificate);
        if (!signed.IsSuccess)
        {
            await transaction.RollbackAsync(cancellationToken);
            return signed.Error;
        }

        var baseName = $"{company.Value.Ruc}-{CompanyType}-{series.Code}-{number}";
        var guide = Guide.Prepare(
            Guid.CreateVersion7(), tenant.Value, company.Value.Id, series, number, issueDate, request.MotiveCode!, request.ModalityCode!,
            $"{request.Recipient.DocumentTypeCode.Trim()}-{request.Recipient.DocumentNumber.Trim()}", request.Recipient.Name.Trim(),
            JsonSerializer.Serialize(request with { IssueDate = issueDate }, Json), baseName, signed.Value.Xml, signed.Value.DigestValue, now);
        db.Guides.Add(guide);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await audit.RecordAsync(new AuditEvent(
            AuditActions.GuideCreated, "gre_guide", guide.Id.ToString("D"), tenant.Value,
            NewValues: new Dictionary<string, object?> { ["number"] = $"{guide.Series}-{guide.Number}", ["motive"] = guide.MotiveCode, ["modality"] = guide.ModalityCode }), cancellationToken);
        return ToDto(guide);
    }

    public async Task<Result<GreDto>> SubmitAsync(Guid id, CancellationToken cancellationToken)
    {
        var guide = await db.Guides.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (guide is null)
        {
            return Missing;
        }

        if (guide.State == GreState.Pending)
        {
            return ToDto(guide); // already with SUNAT: sending it again would be a second submission of the same number
        }

        if (guide.State != GreState.Prepared)
        {
            return Error.Conflict(ErrorCodes.GuideStateInvalid, "Estado inválido", "Solo se envía una guía preparada.");
        }

        var credentials = await CredentialsAsync(guide, cancellationToken);
        if (!credentials.IsSuccess)
        {
            return credentials.Error;
        }

        var zip = packager.Zip(guide.FileBaseName, guide.SignedXml);
        if (!zip.IsSuccess)
        {
            return zip.Error;
        }

        var outcome = await channel.SubmitAsync(new GreSubmission(credentials.Value, CompanyType, guide.Series, guide.Number, guide.FileBaseName, zip.Value), cancellationToken);
        var now = clock.GetUtcNow();
        switch (outcome.Status)
        {
            case GreSubmitStatus.Received:
                guide.MarkPending(outcome.Ticket!, now);
                break;
            case GreSubmitStatus.Refused:
                guide.Fail(outcome.ErrorCode, outcome.ErrorMessage, now);
                break;
            default:
                guide.RecordTransient(outcome.ErrorCode, outcome.ErrorMessage, now);
                break;
        }

        var saved = await TrySaveAsync(cancellationToken);
        if (saved is not null)
        {
            return saved;
        }

        await audit.RecordAsync(new AuditEvent(
            AuditActions.GuideSubmitted, "gre_guide", guide.Id.ToString("D"), guide.TenantId,
            NewValues: new Dictionary<string, object?> { ["number"] = $"{guide.Series}-{guide.Number}", ["status"] = outcome.Status.ToString(), ["errorCode"] = outcome.ErrorCode }), cancellationToken);

        if (outcome.Status == GreSubmitStatus.Transient && guide.State == GreState.Prepared)
        {
            return Error.Conflict(ErrorCodes.GreChannelUnavailable, "SUNAT no respondió", $"La guía queda preparada: vuelva a enviarla. {outcome.ErrorMessage}".Trim());
        }

        // SUNAT usually answers within seconds: ask once now so that the person sees the CDR without waiting for the worker.
        return guide.State == GreState.Pending ? await RefreshAsync(guide.Id, cancellationToken) : ToDto(guide);
    }

    public async Task<Result<GreDto>> RefreshAsync(Guid id, CancellationToken cancellationToken)
    {
        var guide = await db.Guides.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (guide is null)
        {
            return Missing;
        }

        if (guide.IsFinal)
        {
            return ToDto(guide);
        }

        if (guide.State != GreState.Pending || guide.Ticket is null)
        {
            return Error.Conflict(ErrorCodes.GuideStateInvalid, "Estado inválido", "La guía todavía no se envió a SUNAT.");
        }

        var credentials = await CredentialsAsync(guide, cancellationToken);
        if (!credentials.IsSuccess)
        {
            return credentials.Error;
        }

        var answer = await channel.QueryTicketAsync(credentials.Value, guide.Ticket, cancellationToken);
        var now = clock.GetUtcNow();
        switch (answer.Status)
        {
            case GreTicketStatus.Done or GreTicketStatus.Error when answer.CdrZip is not null:
                ApplyCdr(guide, answer.CdrZip, credentials.Value.Ruc, now);
                break;
            case GreTicketStatus.Error:
                guide.Fail(answer.ErrorCode, answer.ErrorMessage, now);
                break;
            case GreTicketStatus.Done:
                guide.ScheduleNextCheck(now); // «correct» without a CDR cannot be accepted: ask again
                break;
            default:
                guide.ScheduleNextCheck(now);
                break;
        }

        var saved = await TrySaveAsync(cancellationToken);
        if (saved is not null)
        {
            return saved;
        }

        if (guide.IsFinal)
        {
            await audit.RecordAsync(new AuditEvent(
                AuditActions.GuideProcessed, "gre_guide", guide.Id.ToString("D"), guide.TenantId,
                NewValues: new Dictionary<string, object?>
                {
                    ["number"] = $"{guide.Series}-{guide.Number}",
                    ["state"] = guide.State.ToString(),
                    ["responseCode"] = guide.CdrResponseCode,
                    ["errorCode"] = guide.ErrorCode,
                }), cancellationToken);
        }

        return ToDto(guide);
    }

    public async Task<Result<GreDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var guide = await db.Guides.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        return guide is null ? Missing : ToDto(guide);
    }

    public async Task<IReadOnlyList<GreDto>> ListAsync(Guid? companyId, GreState? state, int skip, int take, CancellationToken cancellationToken)
    {
        var query = db.Guides.AsNoTracking().AsQueryable();
        if (companyId is { } company)
        {
            query = query.Where(g => g.CompanyId == company);
        }

        if (state is { } wanted)
        {
            query = query.Where(g => g.State == wanted);
        }

        var rows = await query.OrderByDescending(g => g.CreatedAt).ThenByDescending(g => g.Number)
            .Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, MaxPage)).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<string>> GetXmlAsync(Guid id, CancellationToken cancellationToken)
    {
        var xml = await db.Guides.AsNoTracking().Where(g => g.Id == id).Select(g => g.SignedXml).SingleOrDefaultAsync(cancellationToken);
        return xml is null ? Missing : xml;
    }

    public async Task<Result<byte[]>> GetCdrAsync(Guid id, CancellationToken cancellationToken)
    {
        var guide = await db.Guides.AsNoTracking().Where(g => g.Id == id).Select(g => new { g.CdrZip }).SingleOrDefaultAsync(cancellationToken);
        if (guide is null)
        {
            return Missing;
        }

        return guide.CdrZip is { } zip ? zip : Error.NotFound(ErrorCodes.GuideNotFound, "CDR no disponible", "SUNAT todavía no entregó la constancia de esta guía.");
    }

    // ---------- helpers ----------

    private async Task<Result<GreValidationContext>> ContextAsync(string senderRuc, DateOnly today, CancellationToken cancellationToken)
    {
        var lag = await rules.ResolveDecimalAsync(RuleCodes.GreMaxIssueLagDays, "days", today, cancellationToken);
        if (!lag.IsSuccess)
        {
            return lag.Error;
        }

        var generic = await rules.ResolveAsync(RuleCodes.GreGenericMotiveDescriptions, today, cancellationToken);
        var descriptions = new HashSet<string>(StringComparer.Ordinal);
        if (generic.IsSuccess)
        {
            using var document = JsonDocument.Parse(generic.Value.ConfigurationJson);
            if (document.RootElement.TryGetProperty("descriptions", out var list))
            {
                foreach (var item in list.EnumerateArray())
                {
                    descriptions.Add(GreValidator.Normalize(item.GetString() ?? string.Empty));
                }
            }
        }

        async Task<IReadOnlyList<CatalogEntryDto>> EntriesAsync(string number)
        {
            var entries = await catalogs.GetEntriesAsync(number, today, cancellationToken);
            return entries.IsSuccess ? entries.Value : [];
        }

        var related = await EntriesAsync("61");
        return new GreValidationContext(
            senderRuc,
            today,
            (int)lag.Value,
            descriptions,
            new GreCatalogs(
                (await EntriesAsync("03")).Select(e => e.Code).Where(code => code.Length <= 3).ToHashSet(StringComparer.Ordinal),
                (await EntriesAsync("06")).Select(e => e.Code).ToHashSet(StringComparer.Ordinal),
                (await EntriesAsync("18")).Select(e => e.Code).ToHashSet(StringComparer.Ordinal),
                (await EntriesAsync("20")).Select(e => e.Code).ToHashSet(StringComparer.Ordinal),
                related.ToDictionary(e => e.Code, e => e.Metadata.GetValueOrDefault("GRE Aplicable") ?? string.Empty, StringComparer.Ordinal)));
    }

    private async Task<Result<GreChannelCredentials>> CredentialsAsync(Guide guide, CancellationToken cancellationToken)
    {
        var company = await companies.GetAsync(guide.CompanyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        var secret = await solCredentials.GetAsync(guide.CompanyId, cancellationToken);
        if (!secret.IsSuccess)
        {
            return secret.Error;
        }

        if (secret.Value.ApiClientId is null || secret.Value.ApiClientSecret is null)
        {
            return Error.Conflict(
                ErrorCodes.GreChannelNotConfigured, "Faltan las credenciales de API de SUNAT",
                "La guía se envía por la API de SUNAT: registre el client_id y el client_secret de la empresa (SOL, «Credenciales de API SUNAT»).");
        }

        return new GreChannelCredentials(company.Value.Ruc, secret.Value.SolUser, secret.Value.SolPassword, secret.Value.ApiClientId, secret.Value.ApiClientSecret);
    }

    /// <summary>Reads the CDR that SUNAT returned and applies it. A CDR for another guide or another taxpayer is not applied: it is a failed check, not an answer.</summary>
    private void ApplyCdr(Guide guide, byte[] cdrZip, string ruc, DateTimeOffset now)
    {
        var parsed = cdrParser.ParseZip(cdrZip);
        if (!parsed.IsSuccess)
        {
            guide.ScheduleNextCheck(now);
            return;
        }

        var cdr = parsed.Value;
        if (!string.Equals(cdr.ReferenceId, $"{guide.Series}-{guide.Number}", StringComparison.Ordinal) || !string.Equals(cdr.TaxpayerRuc, ruc, StringComparison.Ordinal))
        {
            guide.RecordTransient("SF-GRE-CDR", "El CDR recibido no corresponde a esta guía.", now);
            return;
        }

        var observations = cdr.Observations.Select(o => new GreObservation(o.Code, o.Message)).ToList();
        guide.ApplyCdr(cdrZip, cdr.ProcessId, cdr.ResponseCode, cdr.Description, JsonSerializer.Serialize(observations, Json), observations.Count > 0, now);
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
            return Error.Conflict(ErrorCodes.GreBusy, "Operación concurrente", "Otra operación modificó la guía al mismo tiempo. Consulte su estado e intente de nuevo.");
        }
    }

    internal static GreDto ToDto(Guide g)
    {
        var observations = JsonSerializer.Deserialize<List<GreObservation>>(g.CdrObservationsJson, Json) ?? [];
        return new GreDto(
            g.Id, g.CompanyId, g.Series, g.Number, $"{g.Series}-{g.Number}", g.IssueDate, g.MotiveCode, g.ModalityCode, g.State, g.RecipientDocument, g.RecipientName,
            g.Ticket, g.Attempts, g.CreatedAt, g.SentAt, g.ProcessedAt, g.CdrResponseCode, g.CdrDescription, observations, g.ErrorCode, g.ErrorMessage);
    }
}
