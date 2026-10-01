using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine;

internal sealed partial class ZipCpePackager : ICpePackager
{
    private const long MaxUncompressedBytes = 20 * 1024 * 1024;

    [GeneratedRegex(@"^[A-Za-z0-9._-]{5,80}$")]
    private static partial Regex SafeBaseName();

    public Result<byte[]> Zip(string fileBaseName, string signedXml)
    {
        if (!SafeBaseName().IsMatch(fileBaseName ?? string.Empty) || string.IsNullOrWhiteSpace(signedXml))
        {
            return Error.Validation(ErrorCodes.CpeInvalidDocument, "Paquete inválido", "El nombre base del archivo o el XML no son válidos.");
        }

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(fileBaseName + ".xml", CompressionLevel.Optimal);
            using var writer = entry.Open();
            writer.Write(new UTF8Encoding(false).GetBytes(signedXml));
        }

        return output.ToArray();
    }

    public Result<(string FileName, string Content)> Unzip(byte[] zip)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            // SUNAT's CDR archives carry an empty "dummy/" directory entry next to the XML (seen against the beta service): empty directory
            // entries are ignored, but exactly one real file must remain.
            var files = archive.Entries.Where(e => !(e.FullName.EndsWith('/') && e.Length == 0)).ToList();
            if (files.Count != 1)
            {
                return Bad("El ZIP debe contener exactamente un archivo.");
            }

            var entry = files[0];
            // Guards against zip bombs and path tricks in archives that come from outside.
            if (entry.Length > MaxUncompressedBytes || entry.FullName.IndexOfAny(['/', '\\']) >= 0 || entry.FullName.Contains("..", StringComparison.Ordinal))
            {
                return Bad("El archivo del ZIP es demasiado grande o tiene un nombre no permitido.");
            }

            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false));
            return (entry.Name, reader.ReadToEnd());
        }
        catch (InvalidDataException)
        {
            return Bad("El ZIP está corrupto.");
        }
    }

    private static Error Bad(string detail) => Error.Validation(ErrorCodes.CpeInvalidDocument, "Paquete inválido", detail);
}
