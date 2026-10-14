using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SecureFact.Audit.Contracts;
using SecureFact.Notifications.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Export;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Tenancy.Contracts;
using SecureFact.Tenancy.Infrastructure;

namespace SecureFact.Tenancy.Application;

/// <summary>
/// The data of an account in one ZIP (ADR-074). The ZIP is not the answer of one query: every module that owns data of the account contributes its own tables, through the same scope of the
/// request that every read of the account goes through, so Row Level Security is what keeps another account out of the package.
/// </summary>
internal sealed partial class AccountExport(
    TenancyDbContext db, IDataScope scope, ICurrentUser user, IEnumerable<IAccountDataContributor> contributors, IAuditTrail audit, IBusinessNotices notices, TimeProvider clock, ILogger<AccountExport> logger) : IAccountExport
{
    public async Task<Result<AccountExportPackage>> PrepareAsync(CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenantId)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "La exportación es de una cuenta: requiere un contexto de tenant.");
        }

        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId.Value).Select(t => new { t.Name, t.Status }).SingleOrDefaultAsync(cancellationToken);
        if (tenant is null)
        {
            return Error.NotFound(ErrorCodes.TenantNotFound, "Cuenta no encontrada", "La cuenta no existe.");
        }

        var now = clock.GetUtcNow();
        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), $"securefact-account-{Guid.NewGuid():N}.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await BuildAsync(file, tenant.Name, now, cancellationToken);
            file.Position = 0;
            await audit.RecordAsync(new AuditEvent(AuditActions.AccountExported, "tenant", tenantId.Value.ToString("D"), tenantId.Value, NewValues: new Dictionary<string, object?> { ["areas"] = contributors.Count(), ["bytes"] = file.Length }), cancellationToken);
            await notices.AccountExportedAsync(new AccountExportedNotice(tenantId.Value, Guid.CreateVersion7(), user.UserId, now), cancellationToken);
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }

        return new AccountExportPackage($"cuenta-{tenantId.Value:N}-{now:yyyyMMdd}.zip", file);
    }

    private async Task BuildAsync(Stream output, string accountName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8, cancellationToken);
        await using (zip)
        {
            var writer = new ZipAccountDataWriter(zip);
            await writer.WriteFileAsync("LEEME.txt", Encoding.UTF8.GetBytes(ReadMe(accountName, now)), cancellationToken);
            foreach (var contributor in contributors)
            {
                try
                {
                    await contributor.WriteAsync(writer, cancellationToken);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    LogPartFailed(logger, failure, contributor.GetType().Name);
                    throw;
                }
            }

            await writer.WriteTableAsync("contenido", ["archivo", "filas", "sha256"], writer.Manifest().ToAsyncEnumerable(), cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A part of the account package failed to be written: {Contributor}")]
    private static partial void LogPartFailed(ILogger logger, Exception failure, string contributor);

    private static string ReadMe(string accountName, DateTimeOffset now) => string.Join(
        "\r\n",
        $"Datos de la cuenta «{accountName}»",
        $"Generado: {now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC",
        string.Empty,
        "datos/*.csv     Las tablas de la cuenta (UTF-8; se abren en Excel). Cada celda que empieza con = + - @ se escribe como texto con un apóstrofo delante.",
        "datos/contenido.csv  La lista de las tablas con su cantidad de filas y su huella SHA-256, para comprobar que el paquete está completo.",
        "xml/, cdr/      Los comprobantes electrónicos tal como se firmaron, enviaron y respondió SUNAT (cdr/ con la constancia de recepción).",
        "guias/          Las guías de remisión: xml/ y cdr/ de cada una.",
        string.Empty,
        "No incluye, por seguridad: contraseñas, semillas del segundo factor, certificados digitales y sus claves, credenciales SOL o de la API de SUNAT, claves de la API ni secretos de los webhooks.",
        "No incluye los PDF: se generan a pedido de cada comprobante.",
        "La conservación de los comprobantes y de su constancia es de la empresa emisora: guarde una copia de este paquete.",
        string.Empty);
}

/// <summary>Writes the package as a ZIP, one file at a time, and remembers what it wrote to give the table of contents at the end.</summary>
internal sealed class ZipAccountDataWriter(ZipArchive zip) : IAccountDataWriter
{
    private readonly List<(string Name, long Rows, string Digest)> tables = [];
    private readonly Dictionary<string, long> folders = new(StringComparer.Ordinal);

    public async Task WriteTableAsync(string name, IReadOnlyList<string> columns, IAsyncEnumerable<IReadOnlyList<string?>> rows, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var path = $"datos/{name}.csv";
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        var stream = await entry.OpenAsync(cancellationToken);
        await using (stream)
        {
            await WriteAsync(stream, hash, AccountCsv.ByteOrderMark.ToArray(), cancellationToken);
            await WriteAsync(stream, hash, AccountCsv.Bytes(AccountCsv.Row(columns)), cancellationToken);
            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                if (row.Count != columns.Count)
                {
                    throw new InvalidOperationException($"A row of '{name}' has {row.Count} cells for {columns.Count} columns.");
                }

                await WriteAsync(stream, hash, AccountCsv.Bytes(AccountCsv.Row(row)), cancellationToken);
                count++;
            }
        }

        tables.Add((path, count, Convert.ToHexStringLower(hash.GetHashAndReset())));
    }

    public async Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\', StringComparison.Ordinal) || path.StartsWith('/') || path.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new ArgumentException("The path of a file of the package is relative and stays inside it.", nameof(path));
        }

        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        var stream = await entry.OpenAsync(cancellationToken);
        await using (stream)
        {
            await stream.WriteAsync(content, cancellationToken);
        }

        var slash = path.LastIndexOf('/');
        if (slash > 0)
        {
            var folder = path[..(slash + 1)];
            folders[folder] = folders.GetValueOrDefault(folder) + 1;
        }
    }

    /// <summary>The tables, with their rows and digest, and the folders of files, with how many files each took.</summary>
    public IEnumerable<IReadOnlyList<string?>> Manifest()
    {
        foreach (var (name, rows, digest) in tables)
        {
            yield return [name, rows.ToString(CultureInfo.InvariantCulture), digest];
        }

        foreach (var (folder, files) in folders.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            yield return [folder, files.ToString(CultureInfo.InvariantCulture), null];
        }
    }

    private static async Task WriteAsync(Stream stream, IncrementalHash hash, byte[] bytes, CancellationToken cancellationToken)
    {
        hash.AppendData(bytes);
        await stream.WriteAsync(bytes, cancellationToken);
    }
}
