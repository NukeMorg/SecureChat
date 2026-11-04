using System.Windows;
using SecureChat.Core;

namespace SecureChat.Client.Print;

/// <summary>
/// Опции печати: вложения, водяной знак. Режим (печать/предпросмотр) — через ShowMode.
/// </summary>
public partial class PrintOptionsWindow : Window
{
    public bool IncludeFiles => IncludeFilesCheck.IsChecked == true;
    public bool UseWatermark => WatermarkCheck.IsChecked == true;
    public bool StartPreview { get; private set; }

    public PrintOptionsWindow()
    {
        InitializeComponent();
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        StartPreview = false;
        DialogResult = true;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        StartPreview = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>Сбор строк чата с учётом опции «без вложений».</summary>
    public static List<ChatExportRow> CollectRows(IEnumerable<ChatExportRow> textRows,
        IEnumerable<ChatExportRow>? fileRows, bool includeFiles)
    {
        var rows = new List<ChatExportRow>(textRows);
        if (includeFiles) rows.AddRange(fileRows ?? new List<ChatExportRow>());
        return rows.OrderBy(r => r.TimestampUtc).ToList();
    }
}
