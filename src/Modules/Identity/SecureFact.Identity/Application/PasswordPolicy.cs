using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Identity.Application;

/// <summary>Length-first password policy (NIST SP 800-63B style): no composition rules, a minimum length and a small deny list.</summary>
internal static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    private static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1234", "123456789012", "qwertyuiop12", "contraseña123", "administrador", "securefact123",
    };

    public static Error? Validate(string? password, string email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength || password.Length > MaxLength)
        {
            return Weak($"La contraseña debe tener entre {MinLength} y {MaxLength} caracteres.");
        }

        if (password.Distinct().Count() < 5)
        {
            return Weak("La contraseña es demasiado repetitiva.");
        }

        var localPart = email.Split('@')[0];
        if (Denied.Contains(password) || (localPart.Length >= 4 && password.Contains(localPart, StringComparison.OrdinalIgnoreCase)))
        {
            return Weak("La contraseña es demasiado común o contiene el correo del usuario.");
        }

        return null;
    }

    private static Error Weak(string detail) => Error.Validation(ErrorCodes.WeakPassword, "Contraseña débil", detail);
}
