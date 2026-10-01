using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Contracts;

/// <summary>The ZIP SUNAT receives: exactly one XML whose name equals the ZIP's base name (S04 §1.2–1.3; error 0161 otherwise).</summary>
public interface ICpePackager
{
    Result<byte[]> Zip(string fileBaseName, string signedXml);

    /// <summary>Reads back the single XML of a ZIP (used to inspect what was sent, and to unpack CDR archives).</summary>
    Result<(string FileName, string Content)> Unzip(byte[] zip);
}
