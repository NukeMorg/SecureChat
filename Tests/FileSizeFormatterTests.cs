using System.Globalization;
using SecureChat.Core;
using Xunit;

namespace SecureChat.Tests;

/// <summary>
/// Тесты культурно-зависимого форматирования размера файла (требование 1.8.3):
/// число — по NumberFormatInfo культуры, единицы — передаются из локализованных ресурсов.
/// </summary>
public class FileSizeFormatterTests
{
    private const string B = "Б";
    private const string Kb = "КБ";
    private const string Mb = "МБ";

    private static string Ru(long bytes) =>
        FileSizeFormatter.Format(bytes, B, Kb, Mb, new CultureInfo("ru-RU"));

    private static string En(long bytes) =>
        FileSizeFormatter.Format(bytes, "B", "KB", "MB", new CultureInfo("en-US"));

    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(1, "1 Б")]
    [InlineData(1023, "1\u00A0023 Б")]
    public void Format_Bytes_RuGroupSeparator(long bytes, string expected) =>
        Assert.Equal(expected, Ru(bytes)); // ru: неразрывный пробел-разделитель групп (U+00A0)

    [Fact]
    public void Format_Bytes_EnCommaSeparator() =>
        Assert.Equal("1,023 B", En(1023)); // en: запятая-разделитель групп

    [Fact]
    public void Format_Kilobytes_RuDecimalComma() =>
        Assert.Equal("2,0 КБ", Ru(2048)); // ровно 2 КБ, один десятичный разряд

    [Fact]
    public void Format_Kilobytes_Fractional_EnDecimalPoint() =>
        Assert.Equal("1.5 KB", En(1536)); // en: точка в дробной части

    [Fact]
    public void Format_Megabytes_TwoDecimals()
    {
        Assert.Equal("1,50 МБ", Ru((long)(1.5 * 1024 * 1024) - 1)); // N2 в мегабайтах
        Assert.Equal("1.50 MB", En((long)(1.5 * 1024 * 1024)));
    }

    [Fact]
    public void Format_UsesPassedUnits_WhateverTheyAre()
    {
        // единицы приходят из ресурсов — форматтер не «знает» язык
        Assert.Equal("1,0 Килобайт", FileSizeFormatter.Format(1024, "Байт", "Килобайт", "Мегабайт", new CultureInfo("ru-RU")));
    }
}
