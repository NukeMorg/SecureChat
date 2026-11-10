using System.Windows;
using SecureChat.Client.Services;
using SecureChat.Client.ViewModels;

namespace SecureChat.Client.View;

/// <summary>
/// Представление модального диалога смены пароля; валидация — в <see cref="PasswordDialogViewModel"/>.
/// </summary>
public partial class PasswordDialog : Window
{
    private readonly PasswordDialogViewModel _vm;

    public string CurrentPassword => _vm.CurrentPassword;
    public string NewPassword => _vm.NewPassword;

    public PasswordDialog()
    {
        InitializeComponent();
        _vm = new PasswordDialogViewModel(new DialogService());
        DataContext = _vm;

        // PasswordBox не поддерживает прямую привязку — синхронизируем с ViewModel вручную
        CurrentPasswordBox.PasswordChanged += (_, _) => _vm.CurrentPassword = CurrentPasswordBox.Password;
        NewPasswordBox.PasswordChanged += (_, _) => _vm.NewPassword = NewPasswordBox.Password;
        ConfirmPasswordBox.PasswordChanged += (_, _) => _vm.ConfirmPassword = ConfirmPasswordBox.Password;

        _vm.ConfirmRequested += (_, _) => { DialogResult = true; Close(); };
        _vm.CancelRequested += (_, _) => { DialogResult = false; Close(); };
    }
}
