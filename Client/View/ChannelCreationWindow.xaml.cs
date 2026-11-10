using System.Collections.Generic;
using System.Windows;
using SecureChat.Client.Models;
using SecureChat.Client.Services;
using SecureChat.Client.ViewModels;

namespace SecureChat.Client.View;

/// <summary>
/// Представление диалога создания канала; валидация и фильтрация — в <see cref="ChannelCreationViewModel"/>.
/// </summary>
public partial class ChannelCreationWindow : Window
{
    private readonly ChannelCreationViewModel _vm;

    public string ChannelName => _vm.ChannelName.Trim();
    public List<int> SelectedMemberIds => _vm.SelectedMemberIds;

    public ChannelCreationWindow(IEnumerable<ContactItem> contacts)
    {
        InitializeComponent();
        _vm = new ChannelCreationViewModel(contacts, new DialogService());
        DataContext = _vm;

        _vm.ConfirmRequested += (_, _) => { DialogResult = true; Close(); };
        _vm.CancelRequested += (_, _) => { DialogResult = false; Close(); };
        _vm.FocusNameRequested += (_, _) => ChannelNameTextBox.Focus();

        ChannelNameTextBox.Focus();
    }
}
