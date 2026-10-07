namespace SecureFact.Notifications;

/// <summary>Settings of the outgoing e-mail (<c>Email</c> section). The SMTP password comes from the environment and is never logged or stored in the repository.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public const string ProviderNone = "None";

    public const string ProviderSmtp = "Smtp";

    public const string ProviderSandbox = "Sandbox";

    /// <summary><c>None</c> (nothing is sent), <c>Smtp</c>, or <c>Sandbox</c> (writes each e-mail as a file; for development and tests, refused in production).</summary>
    public string Provider { get; set; } = ProviderNone;

    /// <summary>The address that every e-mail leaves from. Domain authentication (SPF, DKIM) of this address is the operator's.</summary>
    public string? From { get; set; }

    /// <summary>The name shown when a message does not carry a brand of its own.</summary>
    public string FromName { get; set; } = "SecureFact Perú";

    public SmtpSection Smtp { get; set; } = new();

    public SandboxSection Sandbox { get; set; } = new();

    public sealed class SmtpSection
    {
        public string? Host { get; set; }

        public int Port { get; set; } = 587;

        /// <summary><c>StartTls</c> (port 587), <c>Tls</c> (implicit TLS, port 465) or <c>None</c> (plain; only for a local test server, refused in production).</summary>
        public string Security { get; set; } = "StartTls";

        public string? User { get; set; }

        public string? Password { get; set; }

        public int TimeoutSeconds { get; set; } = 15;
    }

    public sealed class SandboxSection
    {
        /// <summary>The directory where the Sandbox provider writes each e-mail as an .eml file.</summary>
        public string? Directory { get; set; }
    }
}
