using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using SecureChat.Client.Services;

namespace SecureChat.Client;

/// <summary>
/// Настройки (Лаба 8): язык (RU/EN, переключение на лету) и тема.
/// Окно строится кодом в стиле существующих диалогов (тема через DynamicResource).
/// </summary>
public class SettingsWindow : Window
{
    private readonly ComboBox _language = new()
    {
        ItemsSource = new[] { "Русский (ru-RU)", "English (en-US)" },
        Height = 26
    };

    private readonly ComboBox _theme = new()
    {
        ItemsSource = new[] { "Тёмная", "Светлая" },
        Height = 26
    };

    public SettingsWindow()
    {
        Title = LocalizationSource.Get("MiSettings");
        Width = 300;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = ((App)Application.Current).MainWindow?.Background ?? SystemColors.WindowBrush;
        Foreground = ((App)Application.Current).MainWindow?.Foreground ?? SystemColors.ControlTextBrush;

        _language.SelectedIndex = App.CurrentLocale == "en-US" ? 1 : 0;
        _theme.SelectedIndex = App.CurrentTheme == "Light" ? 1 : 0;

        var ok = new Button { Content = "OK", Width = 90, Height = 26, Margin = new Thickness(0, 16, 8, 0) };
        ok.Click += (_, _) => { Save(); DialogResult = true; Close(); };
        var cancel = new Button { Content = "Отмена", Width = 90, Height = 26, Margin = new Thickness(0, 16, 0, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddRow(grid, 0, "Язык / Language", _language);
        AddRow(grid, 1, "Тема", _theme);
        Grid.SetRow(buttons, 4);
        grid.Children.Add(buttons);

        Content = grid;
    }

    private static void AddRow(Grid grid, int row, string label, FrameworkElement control)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 4), FontSize = 12 };
        Grid.SetRow(text, row * 2);
        Grid.SetRow(control, row * 2 + 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
    }

    private void Save()
    {
        string lang = _language.SelectedIndex == 1 ? "en-US" : "ru-RU";
        if (lang != App.CurrentLocale) App.ApplyLanguage(lang);

        string theme = _theme.SelectedIndex == 1 ? "Light" : "Dark";
        if (theme != App.CurrentTheme) App.ApplyTheme(theme);
    }
}
