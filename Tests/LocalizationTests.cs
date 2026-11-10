using System.Collections;
using System.Globalization;
using System.Resources;
using SecureChat.Client;
using Xunit;

namespace SecureChat.Tests;

public class LocalizationTests
{
    private static readonly ResourceManager Rm =
        new("SecureChat.Client.Resources.Strings", typeof(LocalizationSource).Assembly);

    [Fact]
    public void Resources_KeyParityBetweenRuAndEn()
    {
        var ruSet = Rm.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!;
        var enSet = Rm.GetResourceSet(new CultureInfo("en"), createIfNotExists: true, tryParents: true)!;

        Assert.NotNull(ruSet);
        Assert.NotNull(enSet);

        var ruKeys = new HashSet<string>(ruSet.Cast<DictionaryEntry>().Select(e => (string)e.Key));
        var enKeys = new HashSet<string>(enSet.Cast<DictionaryEntry>().Select(e => (string)e.Key));

        var missingInEn = ruKeys.Except(enKeys).ToList();
        var missingInRu = enKeys.Except(ruKeys).ToList();

        Assert.True(missingInEn.Count == 0, $"Ключи отсутствуют в Strings.en.resx: {string.Join(", ", missingInEn)}");
        Assert.True(missingInRu.Count == 0, $"Ключи отсутствуют в Strings.resx: {string.Join(", ", missingInRu)}");

        // русский и английский переводы ключевых строк должны отличаться (не копия)
        foreach (var key in new[] { "MiCreateChannel", "CreateChannelTooltip" })
        {
            string ru = ruSet.GetString(key)!;
            string en = enSet.GetString(key)!;
            Assert.NotEqual(ru, en);
        }
    }

    [Fact]
    public void Resources_ValuesAreNonEmpty()
    {
        var ruSet = Rm.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!;
        var enSet = Rm.GetResourceSet(new CultureInfo("en"), createIfNotExists: true, tryParents: true)!;

        foreach (DictionaryEntry entry in ruSet)
        {
            string val = (string)entry.Value!;
            Assert.False(string.IsNullOrWhiteSpace(val), $"Пустое значение для ключа {entry.Key} в ru");
        }

        foreach (DictionaryEntry entry in enSet)
        {
            string val = (string)entry.Value!;
            Assert.False(string.IsNullOrWhiteSpace(val), $"Пустое значение для ключа {entry.Key} в en");
        }
    }

    [Fact]
    public void LocalizationSource_ReturnsKeyForUnknownString()
    {
        string unknown = "NonExistentKey_" + Guid.NewGuid().ToString("N");
        Assert.Equal(unknown, LocalizationSource.Get(unknown));
    }
}
