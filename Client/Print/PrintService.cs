using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using SecureChat.Core;

namespace SecureChat.Client.Print;

/// <summary>
/// Печать чата (требование 1.6, только клиент): FlowDocument + PrintDialog.
/// Шапка (чат, дата, участник), тело (сообщения), номера страниц через обёртку пагинатора.
/// </summary>
public class PrintService
{
    private readonly string _userName;

    public PrintService(string userName)
    {
        _userName = userName;
    }

    /// <summary>Построение FlowDocument: шапка + строки + фон-водяной знак.</summary>
    public FlowDocument BuildDocument(string chatTitle, IReadOnlyList<ChatExportRow> rows, string? watermarkText)
    {
        var culture = CultureInfo.CurrentCulture;

        var doc = new FlowDocument
        {
            PagePadding = new Thickness(60),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12
        };

        if (!string.IsNullOrEmpty(watermarkText))
            doc.Background = CreateWatermarkBrush(watermarkText);

        doc.Blocks.Add(new Paragraph(
            new Run($"SecureChat Enterprise — {chatTitle}"))
        { FontSize = 16, FontWeight = FontWeights.Bold });

        doc.Blocks.Add(new Paragraph(
            new Run($"Напечатал: {_userName} · {DateTime.Now.ToString("g", culture)}"))
        { FontSize = 10, Foreground = Brushes.Gray });

        // разделитель под шапкой (нижняя граница абзаца)
        doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 2, 0, 10), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Brushes.LightGray });

        foreach (var r in rows)
        {
            var p = new Paragraph
            {
                Margin = new Thickness(0, 2, 0, 2),
                Inlines =
                {
                    new Run($"{r.TimestampUtc.ToLocalTime().ToString("g", culture)}  ")
                    { Foreground = Brushes.Gray, FontSize = 10 },
                    new Run(r.Sender) { FontWeight = FontWeights.SemiBold },
                    new Run(":  " + r.Text)
                }
            };
            doc.Blocks.Add(p);
        }

        return doc;
    }

    /// <summary>Диагональный водяной знак «Конфиденциально · …» на фоне каждой страницы.</summary>
    private static Brush CreateWatermarkBrush(string text)
    {
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                42,
                new SolidColorBrush(Color.FromArgb(28, 0x1E, 0x88, 0xE5)),
                1.25);
            ctx.PushTransform(new RotateTransform(-30, 200, 200));
            ctx.DrawText(formatted, new Point(0, 200));
        }

        var brush = new VisualBrush(visual)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 420, 320),
            ViewportUnits = BrushMappingMode.Absolute,
            Opacity = 1
        };
        return brush;
    }
}
