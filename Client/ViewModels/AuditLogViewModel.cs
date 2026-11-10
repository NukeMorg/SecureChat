using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SecureChat.Client.Services;
using SecureChat.Core;

namespace SecureChat.Client.ViewModels;

/// <summary>
/// ViewModel журнала аудита (только Admin): загрузка записей через GetAuditLog,
/// счётчик записей, обработка отказа в доступе (окно закрывается).
/// </summary>
public partial class AuditLogViewModel : ObservableObject
{
    private readonly NetworkService _network;
    private readonly IDialogService _dialogs;

    public ObservableCollection<AuditLogEntry> Entries { get; } = new();

    [ObservableProperty] private string _countText = "";

    /// <summary>Представление закрывает окно (отказ в доступе).</summary>
    public event EventHandler? RequestClose;

    public AuditLogViewModel(NetworkService network, IDialogService dialogs)
    {
        _network = network;
        _dialogs = dialogs;
    }

    public async Task LoadAsync()
    {
        var resp = await _network.GetAuditLogAsync(new GetAuditLogPayload { Limit = 200, Offset = 0 });
        if (resp == null)
        {
            _dialogs.ShowWarning("Недостаточно прав для просмотра журнала аудита.", "Аудит");
            RequestClose?.Invoke(this, EventArgs.Empty);
            return;
        }

        Entries.Clear();
        foreach (var entry in resp.Entries) Entries.Add(entry);
        CountText = $"Записей: {resp.Entries.Count} из {resp.TotalCount}";
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();
}
