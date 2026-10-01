using System.Security.Cryptography.X509Certificates;
using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Contracts;

public enum SignatureHashAlgorithm
{
    /// <summary>RSA-SHA256 / SHA-256. Default: the validation rules do not constrain the algorithm (R-032).</summary>
    Sha256,

    /// <summary>RSA-SHA1 / SHA-1, as in the 2017 guide example. Kept only for compatibility testing against SUNAT.</summary>
    Sha1,
}

/// <param name="Xml">The signed document (the signature sits in <c>ext:ExtensionContent</c>).</param>
/// <param name="DigestValue">Base64 <c>ds:DigestValue</c>; it is the "valor resumen" printed in the QR.</param>
/// <param name="SignatureValue">Base64 <c>ds:SignatureValue</c>.</param>
/// <param name="CertificateThumbprint">SHA-1 thumbprint of the signing certificate (identification only).</param>
public sealed record SignedDocument(string Xml, string DigestValue, string SignatureValue, string CertificateThumbprint);

/// <summary>
/// Enveloped XMLDSig over the whole document, as the Programmer Manual requires (S04 §3.2): one signature, inside
/// <c>ext:UBLExtensions/ext:UBLExtension/ext:ExtensionContent</c>, <c>Reference URI=""</c>, certificate in <c>KeyInfo</c>.
/// </summary>
public interface IXmlSigner
{
    Result<SignedDocument> Sign(string unsignedXml, X509Certificate2 certificate, SignatureHashAlgorithm algorithm = SignatureHashAlgorithm.Sha256);

    /// <summary>Checks that the signature is mathematically valid for the document and reads the embedded certificate. Chain trust is not decided here.</summary>
    Result<SignatureInspection> Verify(string signedXml);
}

public sealed record SignatureInspection(bool IsValid, string CertificateSubject, string CertificateThumbprint, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string DigestValue);
