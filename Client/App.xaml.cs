using System;
using System.Linq;
using System.Windows;
using SecureChat.Client.View;

namespace SecureChat.Client;

public partial class App : Application
{
    // Текущая тема оформления (по умолчанию тёмная)
    public static string CurrentTheme { get; set; } = "Dark";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyTheme(CurrentTheme);

        // Показываем окно входа модально
        var loginWindow = new LoginWindow();
        bool? result = loginWindow.ShowDialog();

        // При успешной авторизации открываем главное окно с данными сессии
        if (result == true && loginWindow.AuthenticatedUserId > 0)
        {
            var mainWindow = new MainWindow(
                loginWindow.AuthenticatedUserId,
                loginWindow.AuthenticatedUserName,
                loginWindow.Token,
                loginWindow.ServerUrl,
                loginWindow.PublicKeyPem,
                loginWindow.PrivateKeyPem,
                loginWindow.Network
            );
            mainWindow.Show();
        }
        else
        {
            // Пользователь закрыл окно входа — завершаем приложение
            Shutdown();
        }
    }

    // Переключение темы: удаляем старый словарь и подключаем новый
    public static void ApplyTheme(string theme)
    {
        CurrentTheme = theme;
        var dicts = Current.Resources.MergedDictionaries;

        // Удаляем ранее подключённые словари тем
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Theme.xaml"))
                dicts.RemoveAt(i);
        }

        // Загружаем выбранную тему
        var themeDict = new ResourceDictionary();
        if (theme == "Light")
            themeDict.Source = new Uri("Themes/LightTheme.xaml", UriKind.Relative);
        else
            themeDict.Source = new Uri("Themes/DarkTheme.xaml", UriKind.Relative);

        dicts.Add(themeDict);
    }
}