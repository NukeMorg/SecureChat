using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SecureChat.Core;
using Xunit;

namespace SecureChat.Tests;

/// <summary>
/// Round-trip тесты экспорта (требование 1.5): один ChatStats → Excel и Word,
/// агрегаты в файлах идентичны расчёту; formula injection обезврежен.
/// </summary>
public class ExportRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "securechat-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static ChatStats SampleStats() =>
        ChatAggregator.Aggregate(new[]
        {
            new ChatExportRow { Id = "m1", Sender = "Аня", Text = "привет =)", TimestampUtc = new DateTime(2026, 1, 1, 9, 15, 0, DateTimeKind.Utc) },
            new ChatExportRow { Id = "m2", Sender = "Борис", Text = "=1+1+1", TimestampUtc = new DateTime(2026, 1, 1, 9, 30, 0, DateTimeKind.Utc) },
            new ChatExportRow { Id = "m3", Sender = "Аня", Text = "обычный текст", TimestampUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc) }
        });

    [Fact]
    public void Excel_RoundTrip_AggregatesMatchAndInjectionNeutralized()
    {
        string path = Path.Combine(_dir, "test.xlsx");
        Directory.CreateDirectory(_dir);
        var stats = SampleStats();

        new ExcelStatsRenderer().Render(stats, path);
        Assert.True(File.Exists(path));

        // чтение обратно через ClosedXML
        using var workbook = new XLWorkbook(path);
        var ws = workbook.Worksheet("Экспорт");

        Assert.Equal(stats.TotalMessages.ToString("N0"), ws.Cell(3, 2).GetString());
        Assert.Equal(stats.ActiveUsers.ToString("N0"), ws.Cell(4, 2).GetString());
        Assert.Equal(stats.TopUser, ws.Cell(5, 2).GetString());

        // formula injection: "=1+1+1" сохранён как ТЕКСТ (префикс ' → quote-prefix), не как формула
        var dangerous = ws.Cell(15, 3);
        Assert.Equal("=1+1+1", dangerous.GetString());
        Assert.Equal(XLDataType.Text, dangerous.DataType);
        Assert.True(string.IsNullOrEmpty(dangerous.FormulaA1));

        // таблица: шапка в строке 13 (сводка 3–6, часы 8–10, пустая строка 12)
        Assert.Equal("ID", ws.Cell(13, 1).GetString());
        Assert.Equal("Отправитель", ws.Cell(13, 2).GetString());
    }

    [Fact]
    public void Word_RoundTrip_DocumentOpensAndContainsAggregates()
    {
        string path = Path.Combine(_dir, "test.docx");
        Directory.CreateDirectory(_dir);
        var stats = SampleStats();

        new WordStatsRenderer().Render(stats, path);
        Assert.True(File.Exists(path));

        // чтение обратно через OpenXML SDK (сам SDK не разбирает недоверенный XML с DTD;
        // для внешних XmlReader-ов применяется WordStatsRenderer.SafeXmlReaderSettings — XXE запрещён)
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart!.Document.Body!;
        string allText = string.Concat(body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(t => t.Text));

        Assert.Contains($"Всего сообщений: {stats.TotalMessages.ToString("N0")}", allText);
        Assert.Contains("Самый активный: Аня", allText);
        Assert.Contains("'=1+1+1", allText); // префикс сохранён как текст (Word не исполняет формулы)
    }
}
