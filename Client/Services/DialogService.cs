using System.Windows;
using System.Windows.Controls;

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

    /// <summary>Модальный ввод строки (title + значение по умолчанию); null — «Отмена».</summary>
    string? Prompt(string title, string defaultText = "");

    /// <summary>Диалог выбора файла для чтения; null — отмена.</summary>
    string? PickFile(string title, string filter);

    /// <summary>Диалог сохранения файла; null — отмена.</summary>
    string? PickSaveFile(string title, string filter, string fileName);
}

/// <summary>Реализация <see cref="IDialogService"/> поверх System.Windows.MessageBox.
/// Все вызовы маршируются на поток UI — безопасны из обработчиков сетевых событий.</summary>
public sealed class DialogService : IDialogService
{
    private static void Ui(Action action) => Ui<object?>(() => { action(); return null; });

    private static T Ui<T>(Func<T> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) return func();
        return dispatcher.Invoke(func);
    }

    public void ShowInfo(string message, string title = "Информация") =>
        Ui(() => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information));

    public void ShowWarning(string message, string title = "Внимание") =>
        Ui(() => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning));

    public void ShowError(string message, string title = "Ошибка") =>
        Ui(() => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error));

    public bool Confirm(string message, string title = "Подтверждение") =>
        Ui(() => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

    public bool ConfirmWarning(string message, string title = "Подтверждение") =>
        Ui(() => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);

    public string? Prompt(string title, string defaultText = "")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 320,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = Application.Current.MainWindow?.Background,
            Foreground = Application.Current.MainWindow?.Foreground
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var textBox = new TextBox { Text = defaultText, Margin = new Thickness(8), VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetRow(textBox, 0);
        grid.Children.Add(textBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        var okBtn = new Button { Content = "OK", Width = 70, Height = 24, Margin = new Thickness(4, 0, 4, 0) };
        var cancelBtn = new Button { Content = "Отмена", Width = 70, Height = 24, Margin = new Thickness(4, 0, 4, 0) };

        bool? result = null;
        okBtn.Click += (_, _) => { result = true; dialog.Close(); };
        cancelBtn.Click += (_, _) => { result = false; dialog.Close(); };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        Grid.SetRow(btnPanel, 1);
        grid.Children.Add(btnPanel);

        dialog.Content = grid;
        dialog.ShowDialog();
        return result == true ? textBox.Text : null;
    }

    public string? PickFile(string title, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string fileName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = fileName };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
