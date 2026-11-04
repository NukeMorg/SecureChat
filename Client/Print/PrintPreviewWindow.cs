using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SecureChat.Client.Print;

/// <summary>
/// Предпросмотр печати: FlowDocument в просмотрщике + кнопка печати (требование 1.6).
/// </summary>
public class PrintPreviewWindow : Window
{
    private readonly FlowDocument _doc;
    private readonly string _title;

    public PrintPreviewWindow(FlowDocument doc, string title)
    {
        _doc = doc;
        _title = title;
        Title = $"Предпросмотр печати — {title}";
        Width = 820;
        Height = 620;

        var printButton = new Button
        {
            Content = "Печать...",
            Width = 100,
            Height = 26,
            Margin = new Thickness(8)
        };
        printButton.Click += PrintButton_Click;

        var viewer = new FlowDocumentScrollViewer { Document = doc };
        var panel = new DockPanel();
        DockPanel.SetDock(printButton, Dock.Bottom);
        panel.Children.Add(printButton);
        panel.Children.Add(viewer);
        Content = panel;
    }

    private void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        _doc.PageWidth = dlg.PrintableAreaWidth;
        _doc.PageHeight = dlg.PrintableAreaHeight;
        var paginator = new PageNumberPaginator(((IDocumentPaginatorSource)_doc).DocumentPaginator)
        {
            PageSize = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight)
        };
        dlg.PrintDocument(paginator, $"SecureChat — {_title}");
    }
}
