using Microsoft.EntityFrameworkCore;
using SecureFact.Identity.Domain;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Identity.Infrastructure;

internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, IDataScope scope)
    : TenantDbContext(options, scope)
{
    public const string Schema = "identity";

    public DbSet<User> Users => Set<User>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<SupportAccessGrant> SupportGrants => Set<SupportAccessGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("app_user");
            b.HasKey(u => u.Id);
            b.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(u => u.ResellerId).HasColumnName("reseller_id");
            b.Property(u => u.Email).HasColumnName("email").HasMaxLength(254).IsRequired();
            b.Property(u => u.EmailNormalized).HasColumnName("email_normalized").HasMaxLength(254).IsRequired();
            b.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(120).IsRequired();
            b.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(300).IsRequired();
            b.Property(u => u.IsActive).HasColumnName("is_active");
            b.Property(u => u.FailedAttempts).HasColumnName("failed_attempts");
            b.Property(u => u.LockedUntil).HasColumnName("locked_until");
            b.Property(u => u.MfaEnabled).HasColumnName("mfa_enabled");
            b.Property(u => u.MfaSecret).HasColumnName("mfa_secret");
            b.Property(u => u.MfaLastStep).HasColumnName("mfa_last_step");
            b.Property(u => u.CreatedAt).HasColumnName("created_at");
            b.Property(u => u.UpdatedAt).HasColumnName("updated_at");
            b.Property(u => u.Version).IsRowVersion();
            b.HasIndex(u => u.EmailNormalized).IsUnique();
            b.HasMany(u => u.Roles).WithOne().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(u => u.Roles).UsePropertyAccessMode(PropertyAccessMode.Field);
            ConfigureOptionalTenantOwned(b);
        });

        modelBuilder.Entity<ApiKey>(b =>
        {
            b.ToTable("api_key");
            b.HasKey(k => k.Id);
            b.Property(k => k.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(k => k.Name).HasColumnName("name").HasMaxLength(60).IsRequired();
            b.Property(k => k.Role).HasColumnName("role").HasMaxLength(64).IsRequired();
            b.Property(k => k.SecretHash).HasColumnName("secret_hash").IsRequired();
            b.Property(k => k.Prefix).HasColumnName("prefix").HasMaxLength(8).IsRequired();
            b.Property(k => k.CreatedBy).HasColumnName("created_by");
            b.Property(k => k.CreatedAt).HasColumnName("created_at");
            b.Property(k => k.ExpiresAt).HasColumnName("expires_at");
            b.Property(k => k.LastUsedAt).HasColumnName("last_used_at");
            b.Property(k => k.RevokedAt).HasColumnName("revoked_at");
            b.Property(k => k.Version).IsRowVersion();
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<SupportAccessGrant>(b =>
        {
            b.ToTable("support_access_grant");
            b.HasKey(g => g.Id);
            b.Property(g => g.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(g => g.GrantedBy).HasColumnName("granted_by");
            b.Property(g => g.CreatedAt).HasColumnName("created_at");
            b.Property(g => g.ExpiresAt).HasColumnName("expires_at");
            b.Property(g => g.RevokedAt).HasColumnName("revoked_at");
            b.Property(g => g.RevokedBy).HasColumnName("revoked_by");
            b.Property(g => g.Note).HasColumnName("note").HasMaxLength(200);
            b.Property(g => g.Version).IsRowVersion();
            b.HasIndex(g => g.TenantId);
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<UserRole>(b =>
        {
            b.ToTable("user_role");
            b.HasKey(r => new { r.UserId, r.RoleCode });
            b.Property(r => r.UserId).HasColumnName("user_id");
            b.Property(r => r.RoleCode).HasColumnName("role_code").HasMaxLength(64);
            b.Property(r => r.AssignedBy).HasColumnName("assigned_by");
            b.Property(r => r.AssignedAt).HasColumnName("assigned_at");
            ConfigureOptionalTenantOwned(b);
        });

        modelBuilder.Entity<UserSession>(b =>
        {
            b.ToTable("user_session");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(s => s.UserId).HasColumnName("user_id");
            b.Property(s => s.FamilyId).HasColumnName("family_id");
            b.Property(s => s.RefreshHash).HasColumnName("refresh_hash").IsRequired();
            b.Property(s => s.CreatedAt).HasColumnName("created_at");
            b.Property(s => s.ExpiresAt).HasColumnName("expires_at");
            b.Property(s => s.AbsoluteExpiresAt).HasColumnName("absolute_expires_at");
            b.Property(s => s.RevokedAt).HasColumnName("revoked_at");
            b.Property(s => s.RevokedReason).HasColumnName("revoked_reason").HasMaxLength(40);
            b.Property(s => s.ReplacedBy).HasColumnName("replaced_by");
            b.Property(s => s.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            b.Property(s => s.UserAgent).HasColumnName("user_agent").HasMaxLength(300);
            b.Property(s => s.SupportGrantId).HasColumnName("support_grant_id");
            b.HasIndex(s => s.SupportGrantId);
            b.HasIndex(s => s.RefreshHash).IsUnique();
            b.HasIndex(s => s.FamilyId);
            b.HasIndex(s => s.UserId);
            ConfigureOptionalTenantOwned(b);
        });

        modelBuilder.Entity<PasswordResetToken>(b =>
        {
            b.ToTable("password_reset_token");
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(t => t.UserId).HasColumnName("user_id");
            b.Property(t => t.TokenHash).HasColumnName("token_hash").IsRequired();
            b.Property(t => t.CreatedAt).HasColumnName("created_at");
            b.Property(t => t.ExpiresAt).HasColumnName("expires_at");
            b.Property(t => t.UsedAt).HasColumnName("used_at");
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.HasIndex(t => t.UserId);
            ConfigureOptionalTenantOwned(b);
        });
    }
}
