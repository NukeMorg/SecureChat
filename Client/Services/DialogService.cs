using System.Windows;

namespace SecureChat.Client.Services;

/// <summary>
/// Абстракция диалоговых окон поверх System.Windows.MessageBox — позволяет
/// ViewModel оставаться независимыми от представления и подменяться в тестах.
/// </summary>
public interface IDialogService
{
    /// <summary>Информационное сообщение с кнопкой OK.</summary>
    void ShowInfo(string message, string title = "Информация");

    /// <summary>Предупреждение с кнопкой OK.</summary>
    void ShowWarning(string message, string title = "Внимание");

    /// <summary>Сообщение об ошибке с кнопкой OK.</summary>
    void ShowError(string message, string title = "Ошибка");

    /// <summary>Вопрос Да/Нет с пиктограммой вопроса.</summary>
    bool Confirm(string message, string title = "Подтверждение");

    /// <summary>Вопрос Да/Нет с пиктограммой предупреждения.</summary>
    bool ConfirmWarning(string message, string title = "Подтверждение");
}

/// <summary>Реализация <see cref="IDialogService"/> поверх System.Windows.MessageBox.</summary>
public sealed class DialogService : IDialogService
{
    public void ShowInfo(string message, string title = "Информация") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string message, string title = "Внимание") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string message, string title = "Ошибка") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title = "Подтверждение") =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool ConfirmWarning(string message, string title = "Подтверждение") =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
