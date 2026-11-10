using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Xps;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Size = System.Windows.Size;
using SecureChat.Client.Models;
using SecureChat.Client.Print;
using SecureChat.Client.Services;
using SecureChat.Client.ViewModels;
using SecureChat.Core;
using Serilog;

namespace SecureChat.Client;

/// <summary>
/// Представление главного окна: вся логика и состояние — в <see cref="MainViewModel"/>.
/// В code-behind остаются только чисто-визуальные вещи: печать (диалоги печати, XPS),
/// фокус полей, прокрутка к найденному сообщению и выбор строки правым кликом.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MainWindow>();

    private readonly MainViewModel _vm;

    public MainWindow(int clientId, string userName, string token, string serverAddress,
        string publicKeyPem, string privateKeyPem, string role, NetworkService network)
    {
        InitializeComponent();
        _vm = new MainViewModel(clientId, userName, token, serverAddress, publicKeyPem, privateKeyPem, role,
            network, new DialogService());
        DataContext = _vm;

        _vm.FocusMessageRequested += (_, _) => MessageTextBox.Focus();
        _vm.FocusSearchRequested += (_, _) => ChatSearchBox.Focus();
        _vm.ScrollToMessageRequested += (_, msg) => MessagesListBox.ScrollIntoView(msg);

        Loaded += async (_, _) => await _vm.InitializeAsync();
    }

    // ---------- Чисто-визуальные обработчики ----------

    /// <summary>Правый клик по строке сообщения — выбираем её, чтобы меню работало с SelectedItem.</summary>
    private void MessageRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && item.DataContext is DialogMessage)
            MessagesListBox.SelectedItem = item.DataContext;
    }

    private void MessageTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.SendTextCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---------- Печать чата (Лаба 4): view-специфичный поток печати ----------

    private void PrintChat_Click(object sender, RoutedEventArgs e)
    {
        var textRows = _vm.CollectCurrentChatRows();
        if (textRows == null)
        {
            MessageBox.Show("Выберите чат для печати.", "Печать",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var fileRows = _vm.CollectCurrentChatFileRows();

        string chatTitle = _vm.GetChatTitle();

        var options = new PrintOptionsWindow { Owner = this };
        if (options.ShowDialog() != true) return;

        var rows = PrintOptionsWindow.CollectRows(textRows, fileRows, options.IncludeFiles);
        if (rows.Count == 0)
        {
            MessageBox.Show("В чате нет сообщений для печати.", "Печать",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? watermark = options.UseWatermark
            ? $"Конфиденциально · {_vm.UserName} · {DateTime.Now.ToString("g", CultureInfo.CurrentCulture)}"
            : null;

        var service = new PrintService(_vm.UserName);
        var doc = service.BuildDocument(chatTitle, rows, watermark);

        if (options.StartPreview)
        {
            new PrintPreviewWindow(doc, chatTitle) { Owner = this }.Show();
            Log.Information("Chat print preview opened {Chat} rows={Rows}", chatTitle, rows.Count);
            return;
        }

        if (options.SaveXps)
        {
            var xpsDlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить как XPS (PDF-альтернатива)",
                Filter = "Документ XPS (*.xps)|*.xps",
                FileName = PathSanitizer.SanitizeFileName($"chat-{DateTime.Now:yyyyMMdd-HHmm}", "chat") + ".xps"
            };
            if (xpsDlg.ShowDialog() != true) return;

            doc.PageWidth = 793.7;  // A4 96dpi
            doc.PageHeight = 1122.5;
            var xpsPaginator = new PageNumberPaginator(((IDocumentPaginatorSource)doc).DocumentPaginator)
            {
                PageSize = new Size(doc.PageWidth, doc.PageHeight)
            };
            using var xps = new System.Windows.Xps.Packaging.XpsDocument(
                xpsDlg.FileName, FileAccess.ReadWrite);
            var xpsWriter = System.Windows.Xps.Packaging.XpsDocument.CreateXpsDocumentWriter(xps);
            xpsWriter.Write(xpsPaginator);
            Log.Information("Chat saved to XPS {File} rows={Rows}", xpsDlg.FileName, rows.Count);
            MessageBox.Show($"Сохранено: {xpsDlg.FileName}", "Экспорт",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var printDialog = new PrintDialog();
        if (printDialog.ShowDialog() != true) return;

        doc.PageWidth = printDialog.PrintableAreaWidth;
        doc.PageHeight = printDialog.PrintableAreaHeight;
        var paginator = new PageNumberPaginator(((IDocumentPaginatorSource)doc).DocumentPaginator)
        {
            PageSize = new Size(printDialog.PrintableAreaWidth, printDialog.PrintableAreaHeight)
        };

        // Отправка на печать через XpsDocumentWriter (WPF): FlowDocument → paginator → XPS-поток принтера.
        // PrintDialog используется только для выбора принтера и параметров.
        PrintQueue? queue = printDialog.PrintQueue;
        var writer = queue != null
            ? PrintQueue.CreateXpsDocumentWriter(queue)
            : PrintQueue.CreateXpsDocumentWriter(queue)!;

        if (printDialog.PrintTicket != null)
            writer.Write(paginator, printDialog.PrintTicket);
        else
            writer.Write(paginator);

        Log.Information("Chat printed via XpsDocumentWriter {Chat} pages={Pages} rows={Rows}",
            chatTitle, paginator.PageCount, rows.Count);
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => _ = _vm.OnWindowClosingAsync();
}
