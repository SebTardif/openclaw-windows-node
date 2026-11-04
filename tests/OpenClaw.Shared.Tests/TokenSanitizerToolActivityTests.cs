using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class TokenSanitizerToolActivityTests
{
    [Fact]
    public void RedactToolActivityLabel_StripsUrlUserInfoAndQuery()
    {
        var label = TokenSanitizer.RedactToolActivityLabel(
            "curl https://user:secret@gateway.example/path?token=stored");

        Assert.DoesNotContain("secret", label);
        Assert.DoesNotContain("token=stored", label);
        Assert.Contains("gateway.example", label);
    }

    [Fact]
    public void RedactToolActivityLabel_RedactsSensitiveCommandOptionsBeforeTruncation()
    {
        var label = TokenSanitizer.RedactToolActivityLabel(
            "tool --token stored-credential-value --name demo");

        Assert.DoesNotContain("stored-credential-value", label);
        Assert.Contains("[REDACTED]", label);
        Assert.Contains("--name demo", label);
    }

    [Fact]
    public void RedactToolActivityLabel_EmptyInputIsEmpty()
    {
        Assert.Equal(string.Empty, TokenSanitizer.RedactToolActivityLabel(null));
        Assert.Equal(string.Empty, TokenSanitizer.RedactToolActivityLabel(""));
    }
}
