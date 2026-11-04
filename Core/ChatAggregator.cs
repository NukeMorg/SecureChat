namespace SecureChat.Core;

/// <summary>Строка экспорта: расшифрованное сообщение чата (только клиент).</summary>
public class ChatExportRow
{
    public string Id { get; init; } = "";
    public string Sender { get; init; } = "";
    public string Text { get; init; } = "";
    public DateTime TimestampUtc { get; init; }
}

/// <summary>Результат агрегации: один расчёт → несколько рендеров (Excel/Word), агрегаты идентичны.</summary>
public class ChatStats
{
    public int TotalMessages { get; init; }
    public int ActiveUsers { get; init; }
    public string TopUser { get; init; } = "";
    public List<KeyValuePair<string, int>> ByHour { get; init; } = new();
    public List<ChatExportRow> Rows { get; init; } = new();
    public DateTime PeriodFromUtc { get; init; }
    public DateTime PeriodToUtc { get; init; }
}

/// <summary>Рендерер статистики в документ (Excel/Word).</summary>
public interface IStatsRenderer
{
    void Render(ChatStats stats, string filePath);
}

/// <summary>
/// Агрегатор чата (требование 1.5.3): единственный расчёт статистики,
/// результаты рендерятся IStatsRenderer-ами. Лимит — 50 000 строк.
/// </summary>
public static class ChatAggregator
{
    public const int MaxRows = 50_000;

    public static ChatStats Aggregate(IEnumerable<ChatExportRow> rows)
    {
        var list = rows.Take(MaxRows).OrderBy(r => r.TimestampUtc).ToList();

        var byUser = list.GroupBy(r => r.Sender)
            .OrderByDescending(g => g.Count())
            .ToList();

        var byHour = list.GroupBy(r => r.TimestampUtc.Hour)
            .OrderBy(g => g.Key)
            .Select(g => new KeyValuePair<string, int>($"{g.Key:00}:00", g.Count()))
            .ToList();

        return new ChatStats
        {
            TotalMessages = list.Count,
            ActiveUsers = byUser.Count,
            TopUser = byUser.FirstOrDefault()?.Key ?? "",
            ByHour = byHour,
            Rows = list,
            PeriodFromUtc = list.Count > 0 ? list[0].TimestampUtc : default,
            PeriodToUtc = list.Count > 0 ? list[^1].TimestampUtc : default
        };
    }
}
