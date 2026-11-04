namespace SecureChat.Core.Printing;

/// <summary>
/// Постраничная разбивка сообщений для печати (требование 1.6).
/// Чистая функция — покрывается unit-тестами без принтера (TDD).
/// </summary>
public static class PagesLayoutEngine
{
    /// <summary>
    /// Разбить строки чата на страницы: ёмкость страницы — linesPerPage «строк текста»;
    /// стоимость сообщения — 1..maxLinesPerMessage строк по ~80 символов.
    /// Порядок строк сохраняется.
    /// </summary>
    public static List<IReadOnlyList<ChatExportRow>> Split(
        IReadOnlyList<ChatExportRow> rows,
        int linesPerPage,
        int maxLinesPerMessage = 4)
    {
        var pages = new List<IReadOnlyList<ChatExportRow>>();
        if (rows.Count == 0 || linesPerPage <= 0) return pages;

        var bucket = new List<ChatExportRow>();
        int used = 0;

        foreach (var row in rows)
        {
            int cost = EstimateLines(row.Text, maxLinesPerMessage);
            if (used + cost > linesPerPage && bucket.Count > 0)
            {
                pages.Add(bucket);
                bucket = new List<ChatExportRow>();
                used = 0;
            }
            bucket.Add(row);
            used += cost;
        }
        if (bucket.Count > 0) pages.Add(bucket);

        return pages;
    }

    /// <summary>Стоимость сообщения в строках: каждые ~80 символов — одна строка (1..max).</summary>
    public static int EstimateLines(string text, int maxLinesPerMessage)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        return Math.Max(1, Math.Min(maxLinesPerMessage, (int)Math.Ceiling(text.Length / 80.0)));
    }
}
