using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.TaxEngine;
using SecureFact.TaxEngine.Contracts;

// One functional round trip against SUNAT's BETA service: build, sign, zip and send a single invoice, then read the CDR.
// Not a load test (the beta must never be stress-tested). Credentials come from the environment and are never printed or stored:
//   SF_BETA_RUC, SF_BETA_USER, SF_BETA_PASSWORD
var ruc = Environment.GetEnvironmentVariable("SF_BETA_RUC");
var user = Environment.GetEnvironmentVariable("SF_BETA_USER");
var password = Environment.GetEnvironmentVariable("SF_BETA_PASSWORD");
if (string.IsNullOrWhiteSpace(ruc) || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("Set SF_BETA_RUC, SF_BETA_USER and SF_BETA_PASSWORD.");
    return 2;
}

var algorithm = Environment.GetEnvironmentVariable("SF_BETA_SIGNATURE") == "sha1" ? SignatureHashAlgorithm.Sha1 : SignatureHashAlgorithm.Sha256;

var services = new ServiceCollection();
services.AddCpeEngineModule();
services.AddTaxEngineModule();
services.AddSunatSubmissionChannel(SunatChannelOptions.Beta);
await using var provider = services.BuildServiceProvider();

// A throw-away self-signed certificate whose subject names the RUC (the beta is for functional tests only).
using var rsa = RSA.Create(2048);
var request = new CertificateRequest($"CN=Prueba Beta, OU={ruc}, O=PRUEBA, C=PE", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
using var certificate = X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx, "x"), "x");

var lima = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Lima"));
var number = (long)lima.TimeOfDay.TotalSeconds + 1;
var totals = provider.GetRequiredService<ITaxCalculator>()
    .Calculate(new TaxCalculationRequest([new TaxableLine(1, 100m, "10")], new TaxRates(0.18m))).Value;
var receipt = args.Contains("boleta", StringComparer.Ordinal);
var data = new UblInvoiceData(
    receipt ? "03" : "01", receipt ? "B001" : "F001", number, DateOnly.FromDateTime(lima.DateTime), TimeOnly.FromDateTime(lima.DateTime), "PEN", "0101",
    new UblParty("6", ruc, "EMPRESA DE PRUEBA SAC", "Prueba"), receipt ? new UblParty("1", "12345678", "CLIENTE DE PRUEBA") : new UblParty("6", "20100066603", "CLIENTE DE PRUEBA SAC"),
    [new UblLine(1, "Servicio de prueba", "ZZ", null, 1, 100m, null, "10")], totals, 0.18m);

if (args.Contains("summary", StringComparer.Ordinal))
{
    return await SummaryRoundTripAsync(provider, certificate, algorithm, ruc, user, password, lima, number);
}

var generated = provider.GetRequiredService<IUblDocumentGenerator>().GenerateInvoice(data);
if (!generated.IsSuccess) { Console.Error.WriteLine($"UBL: {generated.Error.Code} {generated.Error.Detail}"); return 1; }
var signed = provider.GetRequiredService<IXmlSigner>().Sign(generated.Value.Xml, certificate, algorithm);
if (!signed.IsSuccess) { Console.Error.WriteLine($"Sign: {signed.Error.Code} {signed.Error.Detail}"); return 1; }
var zip = provider.GetRequiredService<ICpePackager>().Zip(generated.Value.FileBaseName, signed.Value.Xml);
if (!zip.IsSuccess) { Console.Error.WriteLine($"Zip: {zip.Error.Code} {zip.Error.Detail}"); return 1; }

Console.WriteLine($"Sending {generated.Value.FileBaseName}.zip to the SUNAT BETA service (signature {algorithm})...");
var channel = provider.GetRequiredService<ICpeSubmissionChannel>();
var reply = await channel.SendBillAsync(new SunatCredentials(ruc, user, password), generated.Value.ZipFileName, zip.Value);

Console.WriteLine($"Outcome: {reply.Outcome}");
if (reply.Fault is { } fault)
{
    Console.WriteLine($"Fault: side={fault.Side} code={fault.Code?.ToString(CultureInfo.InvariantCulture)} kind={fault.Kind} retryable={fault.Retryable}");
    Console.WriteLine($"Message: {fault.Message}");
    return 3;
}

if (reply.CdrZip is { } cdrZip)
{
    var cdr = provider.GetRequiredService<ICdrParser>().ParseZip(cdrZip);
    if (!cdr.IsSuccess)
    {
        Console.WriteLine($"CDR could not be parsed: {cdr.Error.Detail}");
        if (provider.GetRequiredService<ICpePackager>().Unzip(cdrZip) is { IsSuccess: true } raw)
        {
            Console.WriteLine(System.Text.RegularExpressions.Regex.Replace(raw.Value.Content, "<ds:Signature.*?</ds:Signature>", "<ds:Signature…/>", System.Text.RegularExpressions.RegexOptions.Singleline));
        }

        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(cdrZip));
        foreach (var entry in archive.Entries)
        {
            Console.WriteLine($"  zip entry: '{entry.FullName}' ({entry.Length} bytes)");
        }

        return 4;
    }
    Console.WriteLine($"CDR: status={cdr.Value.Status} code={cdr.Value.ResponseCode} ref={cdr.Value.ReferenceId} process={cdr.Value.ProcessId}");
    Console.WriteLine($"Description: {cdr.Value.Description}");
    foreach (var note in cdr.Value.Observations)
    {
        Console.WriteLine($"Note {note.Code}: {note.Message}");
    }
}

if (args.Contains("void", StringComparer.Ordinal))
{
    return await VoidRoundTripAsync(provider, certificate, algorithm, ruc, user, password, data);
}

if (args.Contains("nc", StringComparer.Ordinal) || args.Contains("nd", StringComparer.Ordinal))
{
    return await NoteRoundTripAsync(provider, certificate, algorithm, ruc, user, password, lima, number, data, args.Contains("nc", StringComparer.Ordinal));
}

return 0;

static async Task<int> NoteRoundTripAsync(IServiceProvider provider, X509Certificate2 certificate, SignatureHashAlgorithm algorithm, string ruc, string user, string password, DateTimeOffset lima, long number, UblInvoiceData original, bool credit)
{
    var reason = Environment.GetEnvironmentVariable("SF_BETA_REASON") ?? (credit ? "01" : "02");
    var totals = provider.GetRequiredService<ITaxCalculator>().Calculate(new TaxCalculationRequest([new TaxableLine(1, 100m, "10")], new TaxRates(0.18m))).Value;
    var note = new UblNoteData(
        credit ? "07" : "08", original.DocumentTypeCode == "03" ? "BC01" : "FC01", number, original.IssueDate, TimeOnly.FromDateTime(lima.DateTime), "PEN", reason,
        credit ? "Anulacion de la operacion" : "Aumento en el valor", original.DocumentTypeCode, original.Series, original.Number, original.Issuer, original.Buyer,
        [new UblLine(1, "Servicio de prueba", "ZZ", null, 1, 100m, null, "10")], totals, 0.18m);
    var generated = provider.GetRequiredService<IUblDocumentGenerator>().GenerateNote(note);
    if (!generated.IsSuccess) { Console.Error.WriteLine($"UBL note: {generated.Error.Code} {generated.Error.Detail}"); return 1; }
    var signed = provider.GetRequiredService<IXmlSigner>().Sign(generated.Value.Xml, certificate, algorithm);
    if (!signed.IsSuccess) { Console.Error.WriteLine($"Sign: {signed.Error.Code} {signed.Error.Detail}"); return 1; }
    var zip = provider.GetRequiredService<ICpePackager>().Zip(generated.Value.FileBaseName, signed.Value.Xml);
    Console.WriteLine($"Sending note {generated.Value.FileBaseName}.zip (reason {reason})...");
    var reply = await provider.GetRequiredService<ICpeSubmissionChannel>().SendBillAsync(new SunatCredentials(ruc, user, password), generated.Value.ZipFileName, zip.Value);
    Console.WriteLine($"Outcome: {reply.Outcome}");
    if (reply.Fault is { } fault)
    {
        Console.WriteLine($"Fault: side={fault.Side} code={fault.Code?.ToString(CultureInfo.InvariantCulture)} kind={fault.Kind}");
        Console.WriteLine($"Message: {fault.Message}");
        return 3;
    }

    if (reply.CdrZip is { } cdrZip)
    {
        var cdr = provider.GetRequiredService<ICdrParser>().ParseZip(cdrZip);
        if (!cdr.IsSuccess) { Console.WriteLine($"CDR could not be parsed: {cdr.Error.Detail}"); return 4; }
        Console.WriteLine($"CDR: status={cdr.Value.Status} code={cdr.Value.ResponseCode} ref={cdr.Value.ReferenceId}");
        Console.WriteLine($"Description: {cdr.Value.Description}");
        foreach (var n in cdr.Value.Observations)
        {
            Console.WriteLine($"Note {n.Code}: {n.Message}");
        }
    }

    return 0;
}

static async Task<int> SummaryRoundTripAsync(IServiceProvider provider, X509Certificate2 certificate, SignatureHashAlgorithm algorithm, string ruc, string user, string password, DateTimeOffset lima, long number)
{
    var today = DateOnly.FromDateTime(lima.DateTime);
    var reference = Environment.GetEnvironmentVariable("SF_BETA_REFERENCE_DAYS_AGO") is { } ago ? today.AddDays(-int.Parse(ago, CultureInfo.InvariantCulture)) : today;
    var line = new SummaryLineData(1, "B001", number, "1", "12345678", "PEN", 118m, 100m, 0m, 0m, 18m, 0.18m);
    var lines = new List<SummaryLineData> { line };
    if (Environment.GetEnvironmentVariable("SF_BETA_SUMMARY_NOTE") is { } noteKind)
    {
        // A note of the receipt in the same summary: "nc" credit (07) or "nd" debit (08).
        lines.Add(new SummaryLineData(2, "BC01", number, "1", "12345678", "PEN", 118m, 100m, 0m, 0m, 18m, 0.18m, noteKind == "nd" ? "08" : "07", "03", "B001", Environment.GetEnvironmentVariable("SF_BETA_NOTE_UNKNOWN_REF") is null ? number : number + 5_000_000));
    }

    var generated = provider.GetRequiredService<ISummaryDocumentGenerator>().Generate(new SummaryData(ruc, "EMPRESA DE PRUEBA SAC", reference, today, (int)number, lines));
    if (!generated.IsSuccess) { Console.Error.WriteLine($"RC: {generated.Error.Code} {generated.Error.Detail}"); return 1; }
    var signed = provider.GetRequiredService<IXmlSigner>().Sign(generated.Value.Xml, certificate, algorithm);
    if (!signed.IsSuccess) { Console.Error.WriteLine($"Sign: {signed.Error.Code} {signed.Error.Detail}"); return 1; }
    var zip = provider.GetRequiredService<ICpePackager>().Zip(generated.Value.FileBaseName, signed.Value.Xml);
    if (!zip.IsSuccess) { Console.Error.WriteLine($"Zip: {zip.Error.Code} {zip.Error.Detail}"); return 1; }

    var channel = provider.GetRequiredService<ICpeSubmissionChannel>();
    var credentials = new SunatCredentials(ruc, user, password);
    Console.WriteLine($"Sending summary {generated.Value.FileBaseName}.zip to the SUNAT BETA service...");
    var sent = await channel.SendSummaryAsync(credentials, generated.Value.ZipFileName, zip.Value);
    Console.WriteLine($"sendSummary outcome: {sent.Outcome} ticket={sent.Ticket}");
    if (sent.Fault is { } fault)
    {
        Console.WriteLine($"Fault: side={fault.Side} code={fault.Code?.ToString(CultureInfo.InvariantCulture)} kind={fault.Kind} message={fault.Message}");
        return 3;
    }

    if (sent.Ticket is not { } ticket) { return 3; }
    for (var attempt = 1; attempt <= 8; attempt++)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        var status = await channel.GetStatusAsync(credentials, ticket);
        Console.WriteLine($"getStatus #{attempt}: {status.Outcome}");
        if (status.Fault is { } statusFault)
        {
            Console.WriteLine($"Fault: side={statusFault.Side} code={statusFault.Code?.ToString(CultureInfo.InvariantCulture)} message={statusFault.Message}");
            return 3;
        }

        if (status.CdrZip is { } cdrZip)
        {
            var cdr = provider.GetRequiredService<ICdrParser>().ParseZip(cdrZip);
            if (!cdr.IsSuccess)
            {
                Console.WriteLine($"CDR could not be parsed: {cdr.Error.Detail}");
                if (provider.GetRequiredService<ICpePackager>().Unzip(cdrZip) is { IsSuccess: true } raw)
                {
                    Console.WriteLine(System.Text.RegularExpressions.Regex.Replace(raw.Value.Content, "<ds:Signature.*?</ds:Signature>|<Signature.*?</Signature>", "<Signature…/>", System.Text.RegularExpressions.RegexOptions.Singleline));
                }

                return 4;
            }

            Console.WriteLine($"CDR: status={cdr.Value.Status} code={cdr.Value.ResponseCode} ref={cdr.Value.ReferenceId} process={cdr.Value.ProcessId}");
            Console.WriteLine($"Description: {cdr.Value.Description}");
            foreach (var note in cdr.Value.Observations)
            {
                Console.WriteLine($"Note {note.Code}: {note.Message}");
            }

            return 0;
        }
    }

    return 5;
}

static async Task<int> VoidRoundTripAsync(IServiceProvider provider, X509Certificate2 certificate, SignatureHashAlgorithm algorithm, string ruc, string user, string password, UblInvoiceData original)
{
    var today = original.IssueDate;
    var generated = provider.GetRequiredService<IVoidedDocumentsGenerator>().Generate(new VoidedData(
        ruc, "EMPRESA DE PRUEBA SAC", original.IssueDate, today, (int)(original.Number % 90000) + 1, [new VoidedLineData(1, "01", original.Series, original.Number, "Error en la emision de prueba")]));
    if (!generated.IsSuccess) { Console.Error.WriteLine($"RA: {generated.Error.Code} {generated.Error.Detail}"); return 1; }
    var signed = provider.GetRequiredService<IXmlSigner>().Sign(generated.Value.Xml, certificate, algorithm);
    if (!signed.IsSuccess) { Console.Error.WriteLine($"Sign: {signed.Error.Code} {signed.Error.Detail}"); return 1; }
    var zip = provider.GetRequiredService<ICpePackager>().Zip(generated.Value.FileBaseName, signed.Value.Xml);
    var channel = provider.GetRequiredService<ICpeSubmissionChannel>();
    var credentials = new SunatCredentials(ruc, user, password);
    await Task.Delay(TimeSpan.FromSeconds(10)); // the beta answered 401 to a second call made within a few seconds of the first
    Console.WriteLine($"Sending voided-documents communication {generated.Value.FileBaseName}.zip to the SUNAT BETA service...");
    var sent = await channel.SendSummaryAsync(credentials, generated.Value.ZipFileName, zip.Value);
    Console.WriteLine($"sendSummary outcome: {sent.Outcome} ticket={sent.Ticket}");
    if (sent.Fault is { } fault) { Console.WriteLine($"Fault: code={fault.Code?.ToString(CultureInfo.InvariantCulture)} message={fault.Message}"); return 3; }
    for (var attempt = 1; attempt <= 8; attempt++)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        var status = await channel.GetStatusAsync(credentials, sent.Ticket!);
        Console.WriteLine($"getStatus #{attempt}: {status.Outcome}");
        if (status.Fault is { } sf) { Console.WriteLine($"Fault: code={sf.Code?.ToString(CultureInfo.InvariantCulture)} message={sf.Message}"); return 3; }
        if (status.CdrZip is { } cdrZip)
        {
            var cdr = provider.GetRequiredService<ICdrParser>().ParseZip(cdrZip);
            if (!cdr.IsSuccess)
            {
                Console.WriteLine($"CDR could not be parsed: {cdr.Error.Detail}");
                if (provider.GetRequiredService<ICpePackager>().Unzip(cdrZip) is { IsSuccess: true } raw)
                {
                    Console.WriteLine(System.Text.RegularExpressions.Regex.Replace(raw.Value.Content, "<Signature.*?</Signature>", "<Signature…/>", System.Text.RegularExpressions.RegexOptions.Singleline));
                }

                return 4;
            }

            Console.WriteLine($"CDR: status={cdr.Value.Status} code={cdr.Value.ResponseCode} ref={cdr.Value.ReferenceId}");
            Console.WriteLine($"Description: {cdr.Value.Description}");
            foreach (var n in cdr.Value.Observations) { Console.WriteLine($"Note {n.Code}: {n.Message}"); }
            return 0;
        }
    }

    return 5;
}
