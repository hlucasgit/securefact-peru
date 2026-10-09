using System.Net;
using SecureFact.Webhooks.Application;

namespace SecureFact.Unit.Tests.Webhooks;

public class WebhookSecurityTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("203.0.114.5", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.255.255.254", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("10.255.255.255", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.255", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.51.100.7", false)]
    [InlineData("203.0.113.9", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.1.2.3", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    public void An_address_is_public_only_when_it_is_not_one_of_the_reserved_ranges(string address, bool expected)
    {
        Assert.Equal(expected, WebhookTargets.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("https://hooks.cliente.pe/secure-fact")]
    [InlineData("https://hooks.cliente.pe:8443/a/b?x=1")]
    [InlineData("https://8.8.8.8/hook")]
    public void A_public_https_address_is_accepted(string url)
    {
        Assert.Null(WebhookTargets.Validate(url, allowLocal: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hooks.cliente.pe/x")]
    [InlineData("ftp://hooks.cliente.pe/x")]
    [InlineData("http://hooks.cliente.pe/x")]
    [InlineData("https://user:pass@hooks.cliente.pe/x")]
    [InlineData("https://hooks.cliente.pe/x#frag")]
    [InlineData("https://localhost/x")]
    [InlineData("https://app.localhost/x")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://[::1]/x")]
    [InlineData("https://10.1.2.3/x")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://servidor.internal/x")]
    [InlineData("https://impresora.local/x")]
    [InlineData("https://equipo.lan/x")]
    public void An_address_that_is_not_https_or_points_inside_is_refused(string? url)
    {
        Assert.NotNull(WebhookTargets.Validate(url, allowLocal: false));
    }

    [Fact]
    public void An_address_longer_than_the_limit_is_refused()
    {
        Assert.NotNull(WebhookTargets.Validate("https://hooks.cliente.pe/" + new string('a', 500), allowLocal: false));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("http://localhost:8080/hook")]
    public void Where_local_targets_are_allowed_a_local_address_is_accepted_but_credentials_still_are_not(string url)
    {
        Assert.Null(WebhookTargets.Validate(url, allowLocal: true));
        Assert.NotNull(WebhookTargets.Validate(url.Replace("//", "//u:p@", StringComparison.Ordinal), allowLocal: true));
    }

    [Fact]
    public void The_signature_is_the_hexadecimal_hmac_sha256_of_the_timestamp_a_dot_and_the_body()
    {
        // Known answer: HMAC-SHA256 with the key "whsec_test" over "1700000000.{\"a\":1}".
        var signature = WebhookSignature.Sign("whsec_test", 1_700_000_000, "{\"a\":1}");

        Assert.Equal("v1=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData("whsec_test"u8.ToArray(), "1700000000.{\"a\":1}"u8.ToArray())), signature);
        Assert.StartsWith("v1=", signature, StringComparison.Ordinal);
        Assert.Equal(3 + 64, signature.Length);
        Assert.NotEqual(signature, WebhookSignature.Sign("whsec_test", 1_700_000_001, "{\"a\":1}"));
        Assert.NotEqual(signature, WebhookSignature.Sign("whsec_other", 1_700_000_000, "{\"a\":1}"));
        Assert.NotEqual(signature, WebhookSignature.Sign("whsec_test", 1_700_000_000, "{\"a\":2}"));
    }
}
