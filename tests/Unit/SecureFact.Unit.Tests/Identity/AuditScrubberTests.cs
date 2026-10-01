using SecureFact.Audit.Application;

namespace SecureFact.Unit.Tests.Identity;

public class AuditScrubberTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("newPassword")]
    [InlineData("refreshToken")]
    [InlineData("apiKey")]
    [InlineData("pfxPassword")]
    [InlineData("claveSol")]
    [InlineData("passwordHash")]
    [InlineData("Authorization")]
    public void Sensitive_property_names_are_redacted(string key)
    {
        var json = AuditScrubber.ToCanonicalJson(new Dictionary<string, object?> { [key] = "super-secret-value" });

        Assert.DoesNotContain("super-secret-value", json, StringComparison.Ordinal);
        Assert.Contains(AuditScrubber.Redacted, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_is_canonical_regardless_of_insertion_order()
    {
        var a = AuditScrubber.ToCanonicalJson(new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 });
        var b = AuditScrubber.ToCanonicalJson(new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 });

        Assert.Equal(a, b);
        Assert.Equal("{\"a\":1,\"b\":2}", a);
    }

    [Fact]
    public void Empty_input_produces_no_payload() =>
        Assert.Null(AuditScrubber.ToCanonicalJson(new Dictionary<string, object?>()));

    [Fact]
    public void Accented_text_stays_readable() =>
        Assert.Contains("Razón", AuditScrubber.ToCanonicalJson(new Dictionary<string, object?> { ["name"] = "Razón" }), StringComparison.Ordinal);
}
