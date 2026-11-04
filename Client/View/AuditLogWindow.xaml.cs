using System;
using System.Collections.ObjectModel;
using System.Windows;
using SecureChat.Client.Services;
using SecureChat.Core;

namespace SecureChat.Client.View;

/// <summary>
/// Просмотр журнала аудита (только Admin). Данные — из таблицы AuditLog через GetAuditLog.
/// </summary>
public partial class AuditLogWindow : Window
{
    private readonly NetworkService _network;
    private readonly ObservableCollection<AuditLogEntry> _entries = new();

    public AuditLogWindow(NetworkService network)
    {
        InitializeComponent();
        _network = network;
        AuditGrid.ItemsSource = _entries;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var resp = await _network.GetAuditLogAsync(new GetAuditLogPayload { Limit = 200, Offset = 0 });
        if (resp == null)
        {
            MessageBox.Show("Недостаточно прав для просмотра журнала аудита.",
                "Аудит", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        _entries.Clear();
        foreach (var entry in resp.Entries) _entries.Add(entry);
        CountTextBlock.Text = $"Записей: {resp.Entries.Count} из {resp.TotalCount}";
    }
}
