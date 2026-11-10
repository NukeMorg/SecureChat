using System;
using System.Windows;
using SecureChat.Client.Services;
using SecureChat.Client.ViewModels;

namespace SecureChat.Client.View;

/// <summary>
/// Представление журнала аудита (только Admin); логика загрузки — в <see cref="AuditLogViewModel"/>.
/// </summary>
public partial class AuditLogWindow : Window
{
    private readonly AuditLogViewModel _vm;

    public AuditLogWindow(NetworkService network)
    {
        InitializeComponent();
        _vm = new AuditLogViewModel(network, new DialogService());
        DataContext = _vm;

        _vm.RequestClose += (_, _) => Close();
        Loaded += async (_, _) => await _vm.LoadAsync();
    }
}
