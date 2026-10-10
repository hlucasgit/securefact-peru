using SecureFact.Platform.Persistence;

namespace SecureFact.Identity.Domain;

internal sealed class User : IOptionalTenantOwned
{
    private readonly List<UserRole> _roles = [];

    private User()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Null for platform staff.</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>Set for the users of a reseller (role ResellerAdmin); they belong to no tenant. Null for everyone else.</summary>
    public Guid? ResellerId { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public string EmailNormalized { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public int FailedAttempts { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public bool MfaEnabled { get; private set; }

    /// <summary>TOTP seed, envelope-encrypted (ADR-007). Present while enrolment is pending or MFA is enabled.</summary>
    public byte[]? MfaSecret { get; private set; }

    /// <summary>Last accepted TOTP time step; a code for the same or an earlier step is a replay and is rejected.</summary>
    public long MfaLastStep { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public IReadOnlyCollection<UserRole> Roles => _roles;

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    public static User Create(Guid id, Guid? tenantId, string email, string displayName, string passwordHash, DateTimeOffset now, Guid? resellerId = null) => new()
    {
        Id = id,
        TenantId = tenantId,
        ResellerId = resellerId,
        Email = email.Trim(),
        EmailNormalized = Normalize(email),
        DisplayName = displayName.Trim(),
        PasswordHash = passwordHash,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public void RegisterFailedAttempt(DateTimeOffset now, int maxAttempts, TimeSpan lockout)
    {
        FailedAttempts++;
        if (FailedAttempts >= maxAttempts)
        {
            LockedUntil = now + lockout;
            FailedAttempts = 0;
        }

        UpdatedAt = now;
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now)
    {
        FailedAttempts = 0;
        LockedUntil = null;
        UpdatedAt = now;
    }

    public void ChangePassword(string passwordHash, DateTimeOffset now)
    {
        PasswordHash = passwordHash;
        FailedAttempts = 0;
        LockedUntil = null;
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }

    public void StartMfaEnrollment(byte[] protectedSecret, DateTimeOffset now)
    {
        MfaSecret = protectedSecret;
        MfaEnabled = false;
        UpdatedAt = now;
    }

    public void EnableMfa(long acceptedStep, DateTimeOffset now)
    {
        MfaEnabled = true;
        MfaLastStep = acceptedStep;
        UpdatedAt = now;
    }

    public bool TryAcceptMfaStep(long step, DateTimeOffset now)
    {
        if (step <= MfaLastStep)
        {
            return false;
        }

        MfaLastStep = step;
        UpdatedAt = now;
        return true;
    }

    public UserRole AddRole(string role, Guid? assignedBy, DateTimeOffset now)
    {
        var assignment = new UserRole(Id, role, TenantId, assignedBy, now);
        _roles.Add(assignment);
        return assignment;
    }
}

internal sealed class UserRole(Guid userId, string roleCode, Guid? tenantId, Guid? assignedBy, DateTimeOffset assignedAt) : IOptionalTenantOwned
{
    public Guid UserId { get; private set; } = userId;

    public string RoleCode { get; private set; } = roleCode;

    public Guid? TenantId { get; private set; } = tenantId;

    public Guid? AssignedBy { get; private set; } = assignedBy;

    public DateTimeOffset AssignedAt { get; private set; } = assignedAt;
}

internal sealed class UserSession : IOptionalTenantOwned
{
    private UserSession()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid? TenantId { get; private set; }

    /// <summary>Shared by every refresh-token rotation of one login; reuse of a rotated token revokes the whole family.</summary>
    public Guid FamilyId { get; private set; }

    public byte[] RefreshHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset AbsoluteExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public Guid? ReplacedBy { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    /// <summary>The authorization of the account under which a person of the service provider entered it (ADR-069); null for every other session.</summary>
    public Guid? SupportGrantId { get; private set; }

    public static UserSession Start(Guid id, Guid userId, Guid? tenantId, Guid familyId, byte[] refreshHash, DateTimeOffset now, DateTimeOffset expiresAt, DateTimeOffset absoluteExpiresAt, string? ip, string? userAgent) => new()
    {
        Id = id,
        UserId = userId,
        TenantId = tenantId,
        FamilyId = familyId,
        RefreshHash = refreshHash,
        CreatedAt = now,
        ExpiresAt = expiresAt,
        AbsoluteExpiresAt = absoluteExpiresAt,
        IpAddress = Truncate(ip, 64),
        UserAgent = Truncate(userAgent, 300),
    };

    /// <summary>The session of a person of the service provider inside an account. Nobody knows its refresh secret (it is random and discarded), so it cannot be renewed: it ends at <paramref name="expiresAt"/>.</summary>
    public static UserSession StartSupport(Guid id, Guid staffUserId, Guid tenantId, Guid grantId, byte[] unusableRefreshHash, DateTimeOffset now, DateTimeOffset expiresAt, string? ip, string? userAgent)
    {
        var session = Start(id, staffUserId, tenantId, Guid.CreateVersion7(), unusableRefreshHash, now, expiresAt, expiresAt, ip, userAgent);
        session.SupportGrantId = grantId;
        return session;
    }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now && AbsoluteExpiresAt > now;

    public void Revoke(string reason, DateTimeOffset now, Guid? replacedBy = null)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedReason = reason;
        ReplacedBy = replacedBy;
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}

internal sealed class PasswordResetToken : IOptionalTenantOwned
{
    private PasswordResetToken()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid? TenantId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public static PasswordResetToken Issue(Guid userId, Guid? tenantId, byte[] tokenHash, DateTimeOffset now, DateTimeOffset expiresAt) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        TenantId = tenantId,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = expiresAt,
    };

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;

    public void MarkUsed(DateTimeOffset now) => UsedAt = now;
}
