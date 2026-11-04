using System.Drawing;
using System.Drawing.Printing;
using SecureChat.Core;
using SecureChat.Core.Printing;
using System.Globalization;
using Serilog;
using ILogger = Serilog.ILogger;

namespace SecureChat.Client.Print;

/// <summary>
/// Движок печати на WinForms PrintDocument (требование 1.6): программное управление
/// выводом каждой страницы через событие PrintPage — шапка, тело, водяной знак, футер.
/// Разбивка на страницы — детерминированный PagesLayoutEngine (покрыт тестами).
/// </summary>
public class WinFormsPrintService
{
    private readonly string _userName;
    private readonly IReadOnlyList<IReadOnlyList<ChatExportRow>> _pages;
    private readonly string _chatTitle;
    private readonly bool _watermark;
    private int _pageIndex;

    public WinFormsPrintService(string userName, string chatTitle,
        IReadOnlyList<ChatExportRow> rows, bool watermark, int linesPerPage = 44)
    {
        _userName = userName;
        _chatTitle = chatTitle;
        _watermark = watermark;
        _pages = PagesLayoutEngine.Split(rows, linesPerPage);
    }

    public int TotalPages => _pages.Count;

    /// <summary>Собрать PrintDocument: подписка на PrintPage для каждой страницы.</summary>
    public PrintDocument CreateDocument()
    {
        var doc = new PrintDocument { DocumentName = $"SecureChat — {_chatTitle}" };
        _pageIndex = 0;
        doc.PrintPage += OnPrintPage;
        doc.EndPrint += (_, _) => Log.Information(
            "WinForms print finished {Chat} pages={Pages}", _chatTitle, _pages.Count);
        return doc;
    }

    private void OnPrintPage(object? sender, PrintPageEventArgs e)
    {
        if (e.Graphics == null || _pageIndex >= _pages.Count) { e.HasMorePages = false; return; }
        var g = e.Graphics;
        var page = _pages[_pageIndex];
        var culture = CultureInfo.CurrentCulture;
        var bounds = e.MarginBounds;

        if (_watermark)
            DrawWatermark(g, bounds,
                $"Конфиденциально · {_userName} · {DateTime.Now.ToString("g", culture)}");

        float x = bounds.Left;
        float y = bounds.Top;

        // шапка
        using (var titleFont = new Font("Segoe UI", 13, FontStyle.Bold))
        {
            g.DrawString($"SecureChat Enterprise — {_chatTitle}", titleFont, Brushes.Black, x, y);
            y += titleFont.GetHeight(g) + 2;
        }
        using (var smallFont = new Font("Segoe UI", 8))
        {
            g.DrawString($"Напечатал: {_userName} · {DateTime.Now.ToString("g", culture)}",
                smallFont, Brushes.Gray, x, y);
            y += smallFont.GetHeight(g) + 2;
        }
        g.DrawLine(Pens.LightGray, x, y, bounds.Right, y);
        y += 8;

        // тело: время (серым) + отправитель (полужирным) + текст
        using var timeFont = new Font("Segoe UI", 8);
        using var nameFont = new Font("Segoe UI", 9, FontStyle.Bold);
        using var textFont = new Font("Segoe UI", 9);
        float textWidth = bounds.Width - 8;
        foreach (var row in page)
        {
            if (y > bounds.Bottom - 30) break; // защита от переполнения (разбивка — в layout engine)
            string time = row.TimestampUtc.ToLocalTime().ToString("g", culture);
            g.DrawString(time, timeFont, Brushes.Gray, x, y);
            float nameWidth = g.MeasureString(row.Sender + ":  ", nameFont).Width;
            g.DrawString(row.Sender + ":  ", nameFont, Brushes.Black, x, y);

            var rect = new RectangleF(x + nameWidth, y, Math.Max(40, textWidth - nameWidth), bounds.Bottom - y);
            g.DrawString(row.Text, textFont, Brushes.Black, rect);
            y += Math.Max(textFont.GetHeight(g) * 2, g.MeasureString(row.Text, textFont, (int)rect.Width).Height) + 4;
        }

        // футер: номер страницы
        using (var footerFont = new Font("Segoe UI", 8))
        {
            string footer = $"Стр. {_pageIndex + 1} из {_pages.Count}";
            float fx = bounds.Left + (bounds.Width - g.MeasureString(footer, footerFont).Width) / 2;
            g.DrawString(footer, footerFont, Brushes.Gray, fx, bounds.Bottom + 6);
        }

        _pageIndex++;
        e.HasMorePages = _pageIndex < _pages.Count;
    }

    /// <summary>Диагональный полупрозрачный водяной знак «Конфиденциально».</summary>
    private static void DrawWatermark(Graphics g, Rectangle bounds, string text)
    {
        var state = g.Save();
        using var font = new Font("Segoe UI", 26, FontStyle.Bold);
        using var brush = new SolidBrush(Color.FromArgb(24, 0x1E, 0x88, 0xE5));
        g.TranslateTransform(bounds.Left + bounds.Width / 2f, bounds.Top + bounds.Height / 2f);
        g.RotateTransform(-30);
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush, -size.Width / 2, -size.Height / 2);
        g.Restore(state);
    }
}
