using DCScreenSharing.Shared.Logging;
using Xunit;

namespace DCScreenSharing.Core.Tests;

public class SanitizerTests
{
    [Fact]
    public void Sanitize_RedactsPrivateKeyFields()
    {
        var input = "Config PrivateKey = aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI= for endpoint";
        var output = Sanitizer.Sanitize(input);

        Assert.DoesNotContain("aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=", output);
        Assert.Contains("[REDACTED", output);
    }

    [Fact]
    public void Sanitize_RedactsAuthTokens()
    {
        var input = "Sending header: Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.xyz";
        var output = Sanitizer.Sanitize(input);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", output);
        Assert.Contains("[REDACTED_TOKEN]", output);
    }

    [Fact]
    public void Sanitize_PreservesNonSensitiveText()
    {
        var input = "Discord instance app-1.0.9254 detected at LocalAppData";
        var output = Sanitizer.Sanitize(input);

        Assert.Equal(input, output);
    }
}
