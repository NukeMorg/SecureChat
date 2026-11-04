using System.Globalization;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SecureChat.Core;

namespace SecureChat.Client.Services;

/// <summary>
/// Экспорт чата в Word (требование 1.5.2, OpenXML SDK).
/// XXE-защита: все операции чтения XML — только с DtdProcessing.Prohibit и XmlResolver=null.
/// Агрегаты идентичны Excel-рендеру (один ChatAggregator).
/// </summary>
public class WordStatsRenderer : IStatsRenderer
{
    /// <summary>Безопасные настройки чтения XML: DTD запрещён, внешние сущности отключены.</summary>
    public static readonly XmlReaderSettings SafeXmlReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };

    public void Render(ChatStats stats, string filePath)
    {
        var culture = CultureInfo.CurrentCulture;

        using var doc = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document();
        var body = new Body();

        body.Append(new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            new Run(
                new RunProperties(new Bold(), new FontSize { Val = "32" }),
                new Text("SecureChat Enterprise — экспорт чата"))));

        string period = stats.PeriodFromUtc != default
            ? $"Период: {stats.PeriodFromUtc.ToLocalTime():g} — {stats.PeriodToUtc.ToLocalTime():g}. "
            : "";
        body.Append(new Paragraph(new Run(new Text(
            $"{period}Всего сообщений: {stats.TotalMessages.ToString("N0", culture)}. " +
            $"Участников: {stats.ActiveUsers.ToString("N0", culture)}. " +
            $"Самый активный: {CellText.Sanitize(stats.TopUser)}."))));

        body.Append(new Paragraph(new Run(new RunProperties(new Bold()), new Text("Сообщения по часам:"))));
        foreach (var (hour, count) in stats.ByHour)
        {
            body.Append(new Paragraph(new Run(new Text(
                $"{hour} — {count.ToString("N0", culture)}"))));
        }

        body.Append(BuildMessagesTable(stats, culture));

        body.Append(new SectionProperties(
            new PageSize { Width = 11906, Height = 16838 },
            new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134 }));

        main.Document.Append(body);
        main.Document.Save();
    }

    private static Table BuildMessagesTable(ChatStats stats, CultureInfo culture)
    {
        var table = new Table();
        table.Append(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 6 },
                new BottomBorder { Val = BorderValues.Single, Size = 6 },
                new LeftBorder { Val = BorderValues.Single, Size = 6 },
                new RightBorder { Val = BorderValues.Single, Size = 6 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 6 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 6 }),
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }));

        table.Append(BuildRow(("ID", true), ("Отправитель", true), ("Текст", true), ("Дата", true)));

        foreach (var r in stats.Rows)
        {
            table.Append(BuildRow(
                (CellText.Sanitize(r.Id), false),
                (CellText.Sanitize(r.Sender), false),
                (CellText.Sanitize(r.Text), false),
                (r.TimestampUtc.ToLocalTime().ToString("g", culture), false)));
        }

        return table;
    }

    private static TableRow BuildRow(params (string Text, bool Bold)[] cells)
    {
        var row = new TableRow();
        foreach (var (text, bold) in cells)
        {
            var runProps = bold ? new RunProperties(new Bold()) : new RunProperties();
            row.Append(new TableCell(
                new Paragraph(new Run(runProps, new Text(text ?? "")))));
        }
        return row;
    }
}
