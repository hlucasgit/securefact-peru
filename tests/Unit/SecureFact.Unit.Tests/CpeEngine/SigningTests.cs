using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.TaxEngine;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.Unit.Tests.CpeEngine;

public class SigningTests
{
    private readonly XmlDsigSigner _signer = new();
    private readonly UblInvoiceGenerator _generator = new();
    private readonly ZipCpePackager _packager = new();

    internal static X509Certificate2 NewCertificate(string ruc = "20100066603", int keySize = 2048, bool validNow = true)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest($"CN=Representante Demo, OU={ruc}, O=EMISORA DEMO SAC, C=PE", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var start = validNow ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(-400);
        var end = validNow ? DateTimeOffset.UtcNow.AddDays(300) : DateTimeOffset.UtcNow.AddDays(-30);
        using var certificate = request.CreateSelfSigned(start, end);
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx, "pw"), "pw");
    }

    internal static string UnsignedInvoice()
    {
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(2, 100m, "10")], new TaxRates(0.18m))).Value;
        var data = new UblInvoiceData(
            "01", "F001", 1, new DateOnly(2026, 9, 30), new TimeOnly(10, 0, 0), "PEN", "0101",
            new UblParty("6", "20100066603", "EMISORA DEMO SAC"), new UblParty("6", "20100070970", "CLIENTE DEMO SAC"),
            [new UblLine(1, "Servicio", "ZZ", null, 2, 100m, null, "10")], totals, 0.18m);
        return new UblInvoiceGenerator().GenerateInvoice(data).Value.Xml;
    }

    [Theory]
    [InlineData(SignatureHashAlgorithm.Sha256)]
    [InlineData(SignatureHashAlgorithm.Sha1)]
    public void A_signed_document_verifies_and_exposes_the_digest(SignatureHashAlgorithm algorithm)
    {
        using var certificate = NewCertificate();

        var signed = _signer.Sign(UnsignedInvoice(), certificate, algorithm);

        Assert.True(signed.IsSuccess, signed.IsSuccess ? null : signed.Error.Detail);
        var inspection = _signer.Verify(signed.Value.Xml).Value;
        Assert.True(inspection.IsValid);
        Assert.Equal(signed.Value.DigestValue, inspection.DigestValue);
        Assert.Equal(certificate.Thumbprint, inspection.CertificateThumbprint);
        Assert.Contains("OU=20100066603", inspection.CertificateSubject, StringComparison.Ordinal);
    }

    [Fact]
    public void The_signature_follows_the_structure_the_rules_require()
    {
        using var certificate = NewCertificate();
        var xml = XDocument.Parse(_signer.Sign(UnsignedInvoice(), certificate).Value.Xml);
        XNamespace ds = "http://www.w3.org/2000/09/xmldsig#";
        XNamespace ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";

        var signatures = xml.Descendants(ds + "Signature").ToList();
        var signature = Assert.Single(signatures);
        Assert.Equal(ext + "ExtensionContent", signature.Parent!.Name);
        Assert.Equal("SignatureSP", (string?)signature.Attribute("Id"));
        Assert.Equal(string.Empty, (string?)signature.Descendants(ds + "Reference").Single().Attribute("URI"));
        Assert.Contains("enveloped-signature", (string?)signature.Descendants(ds + "Transform").First().Attribute("Algorithm"), StringComparison.Ordinal);
        Assert.NotNull(signature.Descendants(ds + "X509Certificate").SingleOrDefault());
        Assert.NotNull(signature.Descendants(ds + "SignatureValue").SingleOrDefault());
        Assert.Equal("http://www.w3.org/2001/04/xmldsig-more#rsa-sha256", (string?)signature.Descendants(ds + "SignatureMethod").Single().Attribute("Algorithm"));
    }

    [Fact]
    public void Any_change_after_signing_invalidates_the_signature()
    {
        using var certificate = NewCertificate();
        var signed = _signer.Sign(UnsignedInvoice(), certificate).Value.Xml;

        var tampered = signed.Replace("236.00", "136.00", StringComparison.Ordinal);

        Assert.NotEqual(signed, tampered);
        Assert.False(_signer.Verify(tampered).Value.IsValid);
    }

    [Fact]
    public void A_swapped_certificate_does_not_verify()
    {
        using var first = NewCertificate();
        using var second = NewCertificate();
        var signed = XDocument.Parse(_signer.Sign(UnsignedInvoice(), first).Value.Xml);
        XNamespace ds = "http://www.w3.org/2000/09/xmldsig#";
        signed.Descendants(ds + "X509Certificate").Single().Value = Convert.ToBase64String(second.RawData);

        Assert.False(_signer.Verify(signed.ToString(SaveOptions.DisableFormatting)).Value.IsValid);
    }

    [Fact]
    public void Unsuitable_certificates_and_documents_are_refused()
    {
        using var small = NewCertificate(keySize: 1024);
        using var expired = NewCertificate(validNow: false);
        using var good = NewCertificate();
        using var publicOnly = X509CertificateLoader.LoadCertificate(good.RawData);

        Assert.False(_signer.Sign(UnsignedInvoice(), small).IsSuccess);
        Assert.False(_signer.Sign(UnsignedInvoice(), expired).IsSuccess);
        Assert.False(_signer.Sign(UnsignedInvoice(), publicOnly).IsSuccess);
        Assert.False(_signer.Sign("<not-xml", good).IsSuccess);
        Assert.False(_signer.Sign("<Invoice/>", good).IsSuccess);

        var once = _signer.Sign(UnsignedInvoice(), good).Value.Xml;
        Assert.False(_signer.Sign(once, good).IsSuccess); // already signed: the extension is no longer empty
    }

    [Fact]
    public void Signing_does_not_change_the_document_content()
    {
        using var certificate = NewCertificate();
        var unsigned = XDocument.Parse(UnsignedInvoice());
        var signed = XDocument.Parse(_signer.Sign(UnsignedInvoice(), certificate).Value.Xml);
        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

        Assert.Equal(unsigned.Descendants(cbc + "PayableAmount").Single().Value, signed.Descendants(cbc + "PayableAmount").Single().Value);
        Assert.Equal(unsigned.Descendants(cbc + "ID").First().Value, signed.Descendants(cbc + "ID").First().Value);
    }

    // ---------- ZIP ----------

    [Fact]
    public void The_zip_holds_one_xml_named_after_the_package_and_round_trips()
    {
        var zip = _packager.Zip("20100066603-01-F001-1", "<a>ñ</a>").Value;

        var (fileName, content) = _packager.Unzip(zip).Value;

        Assert.Equal("20100066603-01-F001-1.xml", fileName);
        Assert.Equal("<a>ñ</a>", content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../evil")]
    [InlineData("a b")]
    public void Unsafe_package_names_are_rejected(string name) =>
        Assert.False(_packager.Zip(name, "<a/>").IsSuccess);

    [Fact]
    public void Corrupt_or_multi_file_archives_are_rejected()
    {
        Assert.False(_packager.Unzip([1, 2, 3, 4]).IsSuccess);

        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            archive.CreateEntry("a.xml");
            archive.CreateEntry("b.xml");
        }

        Assert.False(_packager.Unzip(stream.ToArray()).IsSuccess);
    }

    [Fact]
    public void Empty_directory_entries_such_as_the_dummy_folder_of_sunat_cdrs_are_ignored()
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            archive.CreateEntry("dummy/");
            using var writer = new StreamWriter(archive.CreateEntry("R-20614754151-01-F001-1.xml").Open());
            writer.Write("<a/>");
        }

        var result = _packager.Unzip(stream.ToArray());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        Assert.Equal("R-20614754151-01-F001-1.xml", result.Value.FileName);
    }

    [Fact]
    public void A_folder_with_content_or_a_second_file_is_still_refused()
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using (var one = new StreamWriter(archive.CreateEntry("a.xml").Open()))
            {
                one.Write("<a/>");
            }

            using var two = new StreamWriter(archive.CreateEntry("dummy/b.xml").Open());
            two.Write("<b/>");
        }

        Assert.False(_packager.Unzip(stream.ToArray()).IsSuccess);
    }

    [Fact]
    public void The_signed_digest_can_be_printed_in_the_qr()
    {
        using var certificate = NewCertificate();
        var signed = _signer.Sign(UnsignedInvoice(), certificate).Value;

        var qr = new QrPayloadGenerator().Build(new QrData(
            "20100066603", "01", "F001", "1", 36m, 236m, new DateOnly(2026, 9, 30), "6", "20100070970", signed.DigestValue));

        Assert.True(qr.IsSuccess);
        Assert.EndsWith("|" + signed.DigestValue, qr.Value, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _signer.Sign("<Invoice/>", certificate).Error.Code);
    }
}
