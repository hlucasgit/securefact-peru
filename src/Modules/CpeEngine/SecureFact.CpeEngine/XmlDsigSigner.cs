using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine;

internal sealed class XmlDsigSigner : IXmlSigner
{
    private const string ExtNamespace = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    public const string SignatureId = "SignatureSP";

    private const string RsaSha256 = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
    private const string Sha256 = "http://www.w3.org/2001/04/xmlenc#sha256";
    private const string RsaSha1 = "http://www.w3.org/2000/09/xmldsig#rsa-sha1";
    private const string Sha1 = "http://www.w3.org/2000/09/xmldsig#sha1";

    public Result<SignedDocument> Sign(string unsignedXml, X509Certificate2 certificate, SignatureHashAlgorithm algorithm = SignatureHashAlgorithm.Sha256)
    {
        if (certificate is null || !certificate.HasPrivateKey)
        {
            return Bad("El certificado no tiene clave privada.");
        }

        var now = DateTimeOffset.UtcNow;
        if (now < certificate.NotBefore || now > certificate.NotAfter)
        {
            return Bad("El certificado no está vigente.");
        }

        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is null || rsa.KeySize < 2048)
        {
            return Bad("Se requiere un certificado RSA de al menos 2048 bits.");
        }

        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            document.LoadXml(unsignedXml);
        }
        catch (XmlException)
        {
            return Bad("El XML a firmar no está bien formado.");
        }

        var manager = new XmlNamespaceManager(document.NameTable);
        manager.AddNamespace("ext", ExtNamespace);
        if (document.SelectNodes("//ext:UBLExtension", manager) is not { Count: 1 } extensions
            || extensions[0]!.SelectSingleNode("ext:ExtensionContent", manager) is not XmlElement content
            || content.HasChildNodes)
        {
            return Bad("El documento debe tener exactamente una extensión de firma vacía (ext:ExtensionContent).");
        }

        var signedXml = new SignedXml(document) { SigningKey = rsa };
        signedXml.Signature.Id = SignatureId;
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigCanonicalizationUrl;
        signedXml.SignedInfo.SignatureMethod = algorithm == SignatureHashAlgorithm.Sha256 ? RsaSha256 : RsaSha1;

        var reference = new Reference { Uri = string.Empty, DigestMethod = algorithm == SignatureHashAlgorithm.Sha256 ? Sha256 : Sha1 };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        signedXml.AddReference(reference);

        var keyInfo = new KeyInfo();
        var x509 = new KeyInfoX509Data(certificate);
        x509.AddSubjectName(certificate.Subject);
        keyInfo.AddClause(x509);
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();
        var element = signedXml.GetXml();
        content.AppendChild(document.ImportNode(element, true));

        return new SignedDocument(
            document.OuterXml,
            Convert.ToBase64String(reference.DigestValue!),
            Convert.ToBase64String(signedXml.SignatureValue!),
            certificate.Thumbprint);
    }

    public Result<SignatureInspection> Verify(string signedXml)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            document.LoadXml(signedXml);
        }
        catch (XmlException)
        {
            return Bad("El XML no está bien formado.");
        }

        var manager = new XmlNamespaceManager(document.NameTable);
        manager.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
        manager.AddNamespace("ext", ExtNamespace);

        var signatures = document.SelectNodes("//ext:UBLExtension/ext:ExtensionContent/ds:Signature", manager);
        if (signatures is not { Count: 1 })
        {
            return Bad("Se esperaba exactamente una firma dentro de ext:ExtensionContent.");
        }

        var signature = (XmlElement)signatures[0]!;
        var xml = new SignedXml(document);
        xml.LoadXml(signature);

        if (xml.SignedInfo!.References.Count != 1 || ((Reference)xml.SignedInfo.References[0]!).Uri != string.Empty)
        {
            return Bad("La firma debe tener una única referencia con URI vacía (documento completo).");
        }

        var certNode = signature.SelectSingleNode("ds:KeyInfo/ds:X509Data/ds:X509Certificate", manager);
        if (certNode is null)
        {
            return Bad("La firma no incluye el certificado.");
        }

        using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certNode.InnerText.Trim()));
        var valid = xml.CheckSignature(certificate, verifySignatureOnly: true);
        var digest = signature.SelectSingleNode("ds:SignedInfo/ds:Reference/ds:DigestValue", manager)?.InnerText.Trim() ?? string.Empty;

        return new SignatureInspection(valid, certificate.Subject, certificate.Thumbprint, certificate.NotBefore, certificate.NotAfter, digest);
    }

    private static Error Bad(string detail) => Error.Validation(ErrorCodes.CpeInvalidDocument, "No se pudo procesar la firma", detail);
}
