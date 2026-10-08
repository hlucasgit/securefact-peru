namespace SecureFact.SharedKernel.Import;

/// <summary>A CSV pasted or uploaded by the user. With <paramref name="Commit"/> false nothing is written: the answer says what would happen (ADR-053).</summary>
public sealed record ImportRequest(string? Csv, bool Commit);

public enum ImportRowStatus
{
    /// <summary>The row is valid and would be created (preview).</summary>
    Ready,

    /// <summary>The row was created.</summary>
    Created,

    /// <summary>A record with the same identity already exists: the row is skipped, nothing is overwritten.</summary>
    Existing,

    /// <summary>The row has a problem (see the message) and is skipped.</summary>
    Invalid,
}

/// <param name="Line">The line of the file where the record starts (the header is line 1).</param>
/// <param name="Key">What identifies the record (document or code), when the row got far enough to have one.</param>
public sealed record ImportRowResult(int Line, ImportRowStatus Status, string? Key, string? Message);

public sealed record ImportResult(bool Committed, int Total, int Ready, int Created, int Existing, int Invalid, IReadOnlyList<ImportRowResult> Rows)
{
    public static ImportResult Of(bool committed, IReadOnlyList<ImportRowResult> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return new ImportResult(
            committed,
            rows.Count,
            rows.Count(r => r.Status == ImportRowStatus.Ready),
            rows.Count(r => r.Status == ImportRowStatus.Created),
            rows.Count(r => r.Status == ImportRowStatus.Existing),
            rows.Count(r => r.Status == ImportRowStatus.Invalid),
            rows);
    }
}

public static class ImportLimits
{
    /// <summary>A file larger than this is refused before it is read (characters, about 1 MB).</summary>
    public const int MaxCharacters = 1_000_000;

    public const int MaxRows = 2000;
}
