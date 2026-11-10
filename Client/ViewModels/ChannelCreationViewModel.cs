using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SecureChat.Client.Models;
using SecureChat.Client.Services;

namespace SecureChat.Client.ViewModels;

/// <summary>
/// ViewModel диалога создания канала: название, выбор участников с поиском
/// по имени/ID, валидация названия.
/// </summary>
public partial class ChannelCreationViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    public ObservableCollection<ContactSelectionItem> AllItems { get; }
    public ICollectionView ItemsView { get; }

    [ObservableProperty] private string _channelName = "";
    [ObservableProperty] private string _searchQuery = "";

    /// <summary>ID выбранных участников (для MainViewModel после закрытия диалога).</summary>
    public List<int> SelectedMemberIds => AllItems.Where(i => i.IsSelected).Select(i => i.Id).ToList();

    /// <summary>Валидация пройдена — представление закрывает окно с DialogResult=true.</summary>
    public event EventHandler? ConfirmRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? FocusNameRequested;

    public ChannelCreationViewModel(IEnumerable<ContactItem> contacts, IDialogService dialogs)
    {
        _dialogs = dialogs;
        AllItems = new ObservableCollection<ContactSelectionItem>(
            contacts.Select(c => new ContactSelectionItem
            {
                Id = c.Id,
                Name = c.Name,
                RawName = c.RawName,
                IsOnline = c.IsOnline,
                IsSelected = false
            })
        );
        ItemsView = CollectionViewSource.GetDefaultView(AllItems);
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        string query = SearchQuery.Trim();
        if (string.IsNullOrEmpty(query))
        {
            ItemsView.Filter = null;
        }
        else
        {
            ItemsView.Filter = item =>
            {
                if (item is ContactSelectionItem ci)
                {
                    return (!string.IsNullOrEmpty(ci.RawName) && ci.RawName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        || ci.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || ci.Id.ToString() == query;
                }
                return false;
            };
        }
        ItemsView.Refresh();
    }

    [RelayCommand]
    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(ChannelName.Trim()))
        {
            _dialogs.ShowWarning("Введите название канала.", "Ошибка");
            FocusNameRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        ConfirmRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);
}
