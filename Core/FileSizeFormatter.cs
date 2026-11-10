using System.Globalization;

namespace SecureChat.Core;

/// <summary>
/// Форматирование размера файла: число — по NumberFormatInfo культуры,
/// единицы измерения передаются извне (берутся из локализованных ресурсов).
/// Пороги: &lt; 1 КБ — байты; &lt; 1 МБ — килобайты (1 КБ = 1024 Б); далее — мегабайты.
/// </summary>
public static class FileSizeFormatter
{
    private const double Kb = 1024d;
    private const double Mb = 1024d * 1024d;

    public static string Format(long bytes, string unitBytes, string unitKilobytes, string unitMegabytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double value = bytes;
        string unit = unitBytes;
        string format = "N0";

        if (bytes >= Mb)
        {
            value = bytes / Mb;
            unit = unitMegabytes;
            format = "N2";
        }
        else if (bytes >= Kb)
        {
            value = bytes / Kb;
            unit = unitKilobytes;
            format = "N1";
        }

        string number = value.ToString(format, culture);
        return $"{number} {unit}";
    }
}
