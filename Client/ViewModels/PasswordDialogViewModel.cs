using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SecureChat.Client.Services;

namespace SecureChat.Client.ViewModels;

/// <summary>
/// ViewModel диалога смены пароля: заполненность полей и совпадение паролей;
/// сложность пароля дополнительно проверяет сервер (Validators.IsValidPassword).
/// </summary>
public partial class PasswordDialogViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    [ObservableProperty] private string _currentPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";

    /// <summary>Валидация пройдена — представление закрывает окно с DialogResult=true.</summary>
    public event EventHandler? ConfirmRequested;
    public event EventHandler? CancelRequested;

    public PasswordDialogViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
    }

    [RelayCommand]
    private void Confirm()
    {
        if (string.IsNullOrEmpty(CurrentPassword))
        {
            _dialogs.ShowWarning("Введите текущий пароль.", "Смена пароля");
            return;
        }
        if (string.IsNullOrEmpty(NewPassword))
        {
            _dialogs.ShowWarning("Введите новый пароль.", "Смена пароля");
            return;
        }
        if (NewPassword != ConfirmPassword)
        {
            _dialogs.ShowWarning("Новые пароли не совпадают.", "Смена пароля");
            return;
        }
        ConfirmRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);
}
