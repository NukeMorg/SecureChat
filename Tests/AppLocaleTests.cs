using SecureChat.Client;
using Xunit;

namespace SecureChat.Tests;

public class AppLocaleTests
{
    [Theory]
    [InlineData("ru", "ru-RU")]
    [InlineData("ru-RU", "ru-RU")]
    [InlineData("ru_RU", "ru-RU")]
    [InlineData("en", "en-US")]
    [InlineData("en-US", "en-US")]
    [InlineData("en_US", "en-US")]
    [InlineData("fr", "ru-RU")]
    [InlineData("de-DE", "ru-RU")]
    [InlineData("", "ru-RU")]
    [InlineData("unknown", "ru-RU")]
    public void NormalizeLocale_MapsToSupportedLocales(string input, string expected)
    {
        string actual = App.NormalizeLocale(input);
        Assert.Equal(expected, actual);
    }
}
