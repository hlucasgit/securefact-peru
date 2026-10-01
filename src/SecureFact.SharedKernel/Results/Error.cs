namespace SecureFact.SharedKernel.Results;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden,
    Unexpected,
}

/// <summary>Stable, user-safe error. <see cref="Code"/> follows the SF-AREA-nnn convention.</summary>
public sealed record Error(string Code, string Title, string Detail, ErrorKind Kind)
{
    public static Error Validation(string code, string title, string detail) => new(code, title, detail, ErrorKind.Validation);

    public static Error NotFound(string code, string title, string detail) => new(code, title, detail, ErrorKind.NotFound);

    public static Error Forbidden(string code, string title, string detail) => new(code, title, detail, ErrorKind.Forbidden);

    public static Error Conflict(string code, string title, string detail) => new(code, title, detail, ErrorKind.Conflict);
}
