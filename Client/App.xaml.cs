using System;
using System.Linq;
using System.Windows;
using SecureChat.Client.Services;
using SecureChat.Client.View;

namespace SecureChat.Client;

/// <summary>
/// Точка входа WPF: применяет тему, открывает окно входа, при успехе — главное окно.
/// </summary>
public partial class App : Application
{
    // Текущая тема оформления ("Dark" | "Light")
    public static string CurrentTheme { get; set; } = "Dark";

    // Единый сетевой сервис на всё время работы приложения (переживает смену окон)
    public static NetworkService Network { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Приложение не должно завершаться при закрытии LoginWindow — только явно
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        ApplyTheme(CurrentTheme);

        // Окно входа использует общий экземпляр NetworkService
        var loginWindow = new LoginWindow(Network);
        bool? result = loginWindow.ShowDialog();

        if (result == true && loginWindow.AuthenticatedUserId > 0)
        {
            // Успешный вход — открываем главное окно с данными сессии
            var mainWindow = new MainWindow(
                loginWindow.AuthenticatedUserId,
                loginWindow.AuthenticatedUserName,
                loginWindow.Token,
                loginWindow.ServerUrl,
                loginWindow.PublicKeyPem,
                loginWindow.PrivateKeyPem,
                Network
            );

            MainWindow = mainWindow;
            // Дальше приложение закрывается вместе с главным окном
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        else
        {
            // Вход отменён/не удался — рвём соединение и выходим
            Network.Disconnect();
            Shutdown();
        }
    }

    /// <summary>
    /// Применяет тему: удаляет прежний словарь тем и подключает новый.
    /// </summary>
    public static void ApplyTheme(string theme)
    {
        CurrentTheme = theme;
        var dicts = Current.Resources.MergedDictionaries;

        // Удаляем все ранее подключённые словари тем (Theme.xaml)
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Theme.xaml"))
                dicts.RemoveAt(i);
        }

        // Подключаем выбранную тему
        var themeDict = new ResourceDictionary();
        if (theme == "Light")
            themeDict.Source = new Uri("Themes/LightTheme.xaml", UriKind.Relative);
        else
            themeDict.Source = new Uri("Themes/DarkTheme.xaml", UriKind.Relative);

        dicts.Add(themeDict);
    }
}