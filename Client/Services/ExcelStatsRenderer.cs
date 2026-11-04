using System.Globalization;
using ClosedXML.Excel;
using SecureChat.Core;

namespace SecureChat.Client.Services;

/// <summary>
/// Экспорт чата в Excel (требование 1.5.1, ClosedXML).
/// Formula injection — через CellText.Sanitize; даты и числа — по CurrentCulture.
/// </summary>
public class ExcelStatsRenderer : IStatsRenderer
{
    public void Render(ChatStats stats, string filePath)
    {
        var culture = CultureInfo.CurrentCulture;
        var dtf = DateTimeFormatInfo.CurrentInfo;

        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Экспорт");

        ws.Cell(1, 1).Value = "SecureChat Enterprise — экспорт чата";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);

        ws.Cell(3, 1).Value = "Всего сообщений";
        ws.Cell(3, 2).Value = stats.TotalMessages.ToString("N0", culture);
        ws.Cell(4, 1).Value = "Участников";
        ws.Cell(4, 2).Value = stats.ActiveUsers.ToString("N0", culture);
        ws.Cell(5, 1).Value = "Самый активный";
        ws.Cell(5, 2).Value = CellText.Sanitize(stats.TopUser);
        if (stats.PeriodFromUtc != default)
        {
            ws.Cell(6, 1).Value = "Период";
            ws.Cell(6, 2).Value = stats.PeriodFromUtc.ToLocalTime().ToString("g", culture) +
                                   " — " + stats.PeriodToUtc.ToLocalTime().ToString("g", culture);
        }

        int row = 8;
        ws.Cell(row, 1).Value = "Час";
        ws.Cell(row, 2).Value = "Сообщений";
        ws.Cell(row, 1).Style.Font.SetBold();
        ws.Cell(row, 2).Style.Font.SetBold();
        row++;
        foreach (var (hour, count) in stats.ByHour)
        {
            ws.Cell(row, 1).Value = hour;
            ws.Cell(row, 2).Value = count.ToString("N0", culture);
            row++;
        }

        row += 2;
        ws.Cell(row, 1).Value = "ID";
        ws.Cell(row, 2).Value = "Отправитель";
        ws.Cell(row, 3).Value = "Текст";
        ws.Cell(row, 4).Value = "Дата";
        ws.Range(row, 1, row, 4).Style.Font.SetBold();
        row++;

        foreach (var r in stats.Rows)
        {
            ws.Cell(row, 1).Value = CellText.Sanitize(r.Id);
            ws.Cell(row, 2).Value = CellText.Sanitize(r.Sender);
            ws.Cell(row, 3).Value = CellText.Sanitize(r.Text);
            var dateCell = ws.Cell(row, 4);
            dateCell.Value = r.TimestampUtc.ToLocalTime();
            dateCell.Style.DateFormat.Format =
                dtf.ShortDatePattern + " " + dtf.ShortTimePattern;
            row++;
        }

        // фиксированные ширины: AdjustToContents на 50 000 строк слишком медленный
        ws.Column(1).Width = 40;
        ws.Column(2).Width = 26;
        ws.Column(3).Width = 90;
        ws.Column(4).Width = 20;

        workbook.SaveAs(filePath);
    }
}
