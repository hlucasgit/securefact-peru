using System.Diagnostics.Metrics;

namespace SecureFact.SharedKernel.Telemetry;

/// <summary>
/// Business metrics shared by all modules (OpenTelemetry <c>Meter</c>). Tags must stay low-cardinality:
/// never put tenant ids, user ids, e-mails or document numbers in a metric tag.
/// </summary>
public static class SecureFactTelemetry
{
    public const string MeterName = "SecureFact";

    private static readonly Meter Meter = new(MeterName, "1.0");

    public static readonly Counter<long> Logins = Meter.CreateCounter<long>(
        "securefact.auth.logins", description: "Login attempts by outcome (succeeded, failed, locked, mfa_required).");

    public static readonly Counter<long> RefreshReuse = Meter.CreateCounter<long>(
        "securefact.auth.refresh_reuse_detected", description: "Rotated refresh tokens presented again (possible token theft).");

    public static readonly Counter<long> AuditEvents = Meter.CreateCounter<long>(
        "securefact.audit.events", description: "Audit events appended, by action.");
}
