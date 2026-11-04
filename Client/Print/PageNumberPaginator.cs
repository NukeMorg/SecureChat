using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;

namespace SecureChat.Client.Print;

/// <summary>
/// Обёртка пагинатора: рисует номер страницы в футере каждой страницы (требование 1.6).
/// Оригинальная страница вставляется как дочерний Visual, поверх — текст футера.
/// </summary>
public class PageNumberPaginator : DocumentPaginator
{
    private readonly DocumentPaginator _inner;

    public PageNumberPaginator(DocumentPaginator inner)
    {
        _inner = inner;
    }

    public override DocumentPage GetPage(int pageNumber)
    {
        var page = _inner.GetPage(pageNumber);

        var container = new ContainerVisual();
        if (page.Visual is Visual content)
            container.Children.Add(content);

        var footer = new DrawingVisual();
        using (var ctx = footer.RenderOpen())
        {
            var text = new FormattedText(
                $"Стр. {pageNumber + 1} из {PageCount}",
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                9,
                Brushes.Gray,
                1.25);
            double x = (page.Size.Width - text.Width) / 2;
            double y = page.Size.Height - 24;
            ctx.DrawText(text, new Point(x, y));
        }
        container.Children.Add(footer);

        return new DocumentPage(container, page.Size, page.BleedBox, page.ContentBox);
    }

    public override bool IsPageCountValid => _inner.IsPageCountValid;
    public override int PageCount => _inner.PageCount;
    public override Size PageSize
    {
        get => _inner.PageSize;
        set => _inner.PageSize = value;
    }
    public override IDocumentPaginatorSource? Source => _inner.Source;
}
