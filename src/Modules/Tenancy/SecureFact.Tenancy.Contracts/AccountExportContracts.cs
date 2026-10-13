using SecureFact.SharedKernel.Results;

namespace SecureFact.Tenancy.Contracts;

/// <summary>The package of an account, complete and ready to be read from its start. Whoever receives it owns the stream: closing it also deletes the file behind it.</summary>
public sealed class AccountExportPackage(string fileName, Stream content) : IAsyncDisposable
{
    public string FileName { get; } = fileName;

    public Stream Content { get; } = content;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>An account takes its data with it (ADR-074): everything it owns, as spreadsheets and the signed files, in one ZIP.</summary>
public interface IAccountExport
{
    /// <summary>
    /// Checks that the caller is inside an account, builds the package and records the export in the audit trail. The package is built on a temporary file as the tables are read, so an account of
    /// any size is never held in memory, and a failure while building it is a failure of the request, not a broken download.
    /// </summary>
    Task<Result<AccountExportPackage>> PrepareAsync(CancellationToken cancellationToken);
}
