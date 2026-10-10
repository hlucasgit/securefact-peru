using SecureFact.Identity.Application;
using SecureFact.Identity.Contracts;

namespace SecureFact.Unit.Tests.Identity;

public class TotpTests
{
    private static readonly byte[] Rfc6238Secret = "12345678901234567890"u8.ToArray();

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    public void Matches_the_RFC_6238_test_vectors(long unixSeconds, string expectedEightDigits) =>
        Assert.Equal(expectedEightDigits, Totp.Compute(Rfc6238Secret, unixSeconds / 30, digits: 8));

    [Fact]
    public void Verify_accepts_adjacent_steps_and_rejects_far_ones()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = now.ToUnixTimeSeconds() / 30;

        Assert.Equal(step, Totp.Verify(Rfc6238Secret, Totp.Compute(Rfc6238Secret, step), now));
        Assert.Equal(step - 1, Totp.Verify(Rfc6238Secret, Totp.Compute(Rfc6238Secret, step - 1), now));
        Assert.Null(Totp.Verify(Rfc6238Secret, Totp.Compute(Rfc6238Secret, step + 5), now));
        Assert.Null(Totp.Verify(Rfc6238Secret, "abc123", now));
        Assert.Null(Totp.Verify(Rfc6238Secret, null, now));
    }

    [Fact]
    public void Base32_matches_the_RFC_4648_vector() =>
        Assert.Equal("MZXW6YTBOI", Totp.ToBase32("foobar"u8));
}

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("short1!")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("password1234")]
    [InlineData("maria.lopez-2026!")]
    public void Weak_passwords_are_rejected(string password) =>
        Assert.NotNull(PasswordPolicy.Validate(password, "maria.lopez@empresa.pe"));

    [Fact]
    public void A_long_unique_passphrase_is_accepted() =>
        Assert.Null(PasswordPolicy.Validate("correct horse battery staple", "maria@empresa.pe"));
}

public class RoleCatalogTests
{
    [Fact]
    public void Every_role_only_references_known_permissions()
    {
        foreach (var role in RoleCatalog.AllRoles)
        {
            Assert.All(RoleCatalog.PermissionsOf(role), p => Assert.Contains(p, Permissions.All));
        }
    }

    [Fact]
    public void Roles_are_explicit_bundles_not_implied_superusers()
    {
        Assert.DoesNotContain(Permissions.TenantsCreate, RoleCatalog.PermissionsOf(Roles.TenantOwner));
        Assert.DoesNotContain(Permissions.AuditRead, RoleCatalog.PermissionsOf(Roles.TenantAdmin));
        Assert.Contains(Permissions.TenantsCreate, RoleCatalog.PermissionsOf(Roles.PlatformSuperAdmin));
    }

    [Theory]
    [InlineData(Roles.TenantOwner, false, Roles.TenantAdmin, true)]
    [InlineData(Roles.TenantAdmin, false, Roles.TenantOwner, false)]
    [InlineData(Roles.TenantOwner, false, Roles.PlatformSupport, false)]
    [InlineData(Roles.TenantOwner, false, Roles.PlatformSuperAdmin, false)]
    [InlineData(Roles.PlatformSuperAdmin, true, Roles.PlatformSupport, true)]
    [InlineData(Roles.PlatformSupport, true, Roles.PlatformSuperAdmin, false)]
    [InlineData(Roles.TenantOwner, false, "NoSuchRole", false)]
    public void Assignment_never_escalates_privileges(string actorRole, bool actorIsPlatform, string target, bool expected) =>
        Assert.Equal(expected, RoleCatalog.CanAssign([actorRole], actorIsPlatform, target));

    [Fact]
    public void The_role_of_the_person_who_supports_only_reads_and_nobody_can_be_given_it()
    {
        var permissions = RoleCatalog.PermissionsOf(Roles.SupportViewer);

        Assert.All(permissions, p => Assert.EndsWith(".read", p));
        Assert.DoesNotContain(Permissions.UsersManage, permissions);
        Assert.DoesNotContain(Permissions.ApiKeysManage, permissions);
        Assert.DoesNotContain(Permissions.WebhooksManage, permissions);
        Assert.DoesNotContain(Permissions.SupportGrant, permissions);
        Assert.DoesNotContain(Permissions.CertificatesManage, permissions);
        Assert.DoesNotContain(Permissions.CpeSend, permissions);
        foreach (var actor in new[] { Roles.PlatformSuperAdmin, Roles.TenantOwner })
        {
            Assert.False(RoleCatalog.CanAssign([actor], actor == Roles.PlatformSuperAdmin, Roles.SupportViewer));
        }
    }

    [Theory]
    [InlineData(Roles.PlatformSupport, Permissions.SupportAccess, true)]
    [InlineData(Roles.ResellerAdmin, Permissions.SupportAccess, true)]
    [InlineData(Roles.TenantOwner, Permissions.SupportAccess, false)]
    [InlineData(Roles.TenantOwner, Permissions.SupportGrant, true)]
    [InlineData(Roles.TenantAdmin, Permissions.SupportGrant, false)]
    [InlineData(Roles.PlatformSupport, Permissions.SupportGrant, false)]
    public void Only_the_owner_authorizes_and_only_the_service_provider_enters(string role, string permission, bool expected) =>
        Assert.Equal(expected, RoleCatalog.PermissionsOf(role).Contains(permission));
}
