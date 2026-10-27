using AstraLauncher.Authentication;

namespace AstraLauncher.Tests;

public sealed class DpapiTokenProtectorTests
{
    private const string Plaintext = "M.C530_BAY.0.U.-refresh-token-0123456789";

    [Fact]
    public void ProtectThenUnprotect_RoundTripsPlaintext()
    {
        var protector = new DpapiTokenProtector();

        string protectedText = protector.Protect(Plaintext);

        Assert.NotEqual(Plaintext, protectedText);
        Assert.Equal(Plaintext, protector.Unprotect(protectedText));
    }

    [Fact]
    public void Protect_ReturnsBase64CiphertextThatDoesNotContainPlaintext()
    {
        var protector = new DpapiTokenProtector();

        string protectedText = protector.Protect(Plaintext);

        byte[] ciphertext = Convert.FromBase64String(protectedText);
        Assert.NotEmpty(ciphertext);
        Assert.DoesNotContain(Plaintext, protectedText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Protect_RejectsBlankText(string plaintext) =>
        Assert.Throws<ArgumentException>(() => new DpapiTokenProtector().Protect(plaintext));

    [Fact]
    public void Protect_RejectsNullText() =>
        Assert.ThrowsAny<ArgumentException>(() => new DpapiTokenProtector().Protect(null!));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Unprotect_RejectsBlankText(string protectedText) =>
        Assert.Throws<ArgumentException>(() => new DpapiTokenProtector().Unprotect(protectedText));

    [Fact]
    public void Unprotect_RejectsNullText() =>
        Assert.ThrowsAny<ArgumentException>(() => new DpapiTokenProtector().Unprotect(null!));

    [Theory]
    [InlineData("not-a-valid-base64-payload!")]
    [InlineData("AQIDBA==")]
    public void Unprotect_WithForgedCiphertext_ThrowsInvalidOperationException(string forgedText)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new DpapiTokenProtector().Unprotect(forgedText));

        Assert.Contains("令牌", exception.Message, StringComparison.Ordinal);
    }
}
