using SecureChat.Core;
using Xunit;

namespace SecureChat.Tests;

public class TextNormalizerTests
{
    [Fact]
    public void NormalizeToNfc_ComposesCombiningCharacters_Latin()
    {
        // 'e' + combining acute accent (\u0301) -> 'é' (\u00e9)
        string decomposed = "cafe\u0301";
        string normalized = TextNormalizer.NormalizeToNfc(decomposed);

        Assert.Equal("café", normalized);
        Assert.Equal(4, normalized.Length);
        Assert.Equal(5, decomposed.Length);
    }

    [Fact]
    public void NormalizeToNfc_ComposesCombiningCharacters_Cyrillic()
    {
        // 'и' (\u0438) + combining breve (\u0306) -> 'й' (\u0439)
        string decomposed = "\u0438\u0306";
        string normalized = TextNormalizer.NormalizeToNfc(decomposed);

        Assert.Equal("й", normalized);
        Assert.Equal(1, normalized.Length);
        Assert.Equal(2, decomposed.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Простой русский текст без диакритики")]
    [InlineData("Standard ASCII message 12345!@#$%")]
    public void NormalizeToNfc_PreservesRegularText(string text)
    {
        string normalized = TextNormalizer.NormalizeToNfc(text);
        Assert.Equal(text, normalized);
    }

    [Fact]
    public void NormalizeToNfc_IsIdempotent()
    {
        string text = "cafe\u0301 и\u0306 тест";
        string pass1 = TextNormalizer.NormalizeToNfc(text);
        string pass2 = TextNormalizer.NormalizeToNfc(pass1);

        Assert.Equal(pass1, pass2);
    }
}
