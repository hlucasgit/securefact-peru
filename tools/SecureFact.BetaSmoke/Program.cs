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
var data = new UblInvoiceData(
    "01", "F001", number, DateOnly.FromDateTime(lima.DateTime), TimeOnly.FromDateTime(lima.DateTime), "PEN", "0101",
    new UblParty("6", ruc, "EMPRESA DE PRUEBA SAC", "Prueba"), new UblParty("6", "20100066603", "CLIENTE DE PRUEBA SAC"),
    [new UblLine(1, "Servicio de prueba", "ZZ", null, 1, 100m, null, "10")], totals, 0.18m);

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

return 0;
