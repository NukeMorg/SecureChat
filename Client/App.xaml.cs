using System;
using System.Windows;
using SecureChat.Client.Services;
using SecureChat.Client.View;
using AppSettings = SecureChat.Client.Properties.Settings;

namespace SecureChat.Client;

/// <summary>
/// Точка входа WPF: применяет сохранённую тему, пробует авто-вход по сохранённому токену,
/// иначе показывает окно входа. Тема хранится в Properties.Settings (user.config).
/// </summary>
public partial class App : Application
{
    // Текущая тема оформления ("Dark" | "Light"). Источник истины — AppSettings.Default.Theme.
    public static string CurrentTheme { get; set; } = "Dark";

    // Единый сетевой сервис на всё время работы приложения (переживает смену окон)
    public static NetworkService Network { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Приложение не должно завершаться при закрытии LoginWindow — только явно
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 1) Читаем сохранённую тему из user.config (или дефолт "Dark")
        CurrentTheme = LoadThemeFromSettings();
        ApplyThemeInternal(CurrentTheme, saveToSettings: false);

        // 2) Авто-вход (если в config.json RememberMe="true")
        var saved = LoginWindow.ReadAutoLoginConfig();
        if (saved != null)
        {
            var session = LoginWindow.TryAutoLoginBlocking(Network, saved, timeoutMs: 5000);
            if (session != null)
            {
                var mainWindow = new MainWindow(
                    session.UserId,
                    session.UserName,
                    session.Token,
                    LoginWindow.StaticServerUrl,
                    session.PublicKey,
                    session.PrivateKey,
                    Network);

                MainWindow = mainWindow;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                mainWindow.Show();
                return;
            }
            // Не удалось — падаем в обычный сценарий с окном входа
        }

        // 3) Обычный вход
        var loginWindow = new LoginWindow(Network);
        bool? result = loginWindow.ShowDialog();

        if (result == true && loginWindow.AuthenticatedUserId > 0)
        {
            var mainWindow = new MainWindow(
                loginWindow.AuthenticatedUserId,
                loginWindow.AuthenticatedUserName,
                loginWindow.Token,
                loginWindow.ServerUrl,
                loginWindow.PublicKeyPem,
                loginWindow.PrivateKeyPem,
                Network);

            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        else
        {
            Network.Disconnect();
            Shutdown();
        }
    }

    /// <summary>
    /// Применяет тему и СОХРАНЯЕТ её в user.config.
    /// Вызывается из MainWindow при переключении темы.
    /// </summary>
    public static void ApplyTheme(string theme) => ApplyThemeInternal(theme, saveToSettings: true);

    private static void ApplyThemeInternal(string theme, bool saveToSettings)
    {
        if (theme != "Dark" && theme != "Light") theme = "Dark";
        bool changed = CurrentTheme != theme;
        CurrentTheme = theme;

        var dicts = Current.Resources.MergedDictionaries;
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Theme.xaml"))
                dicts.RemoveAt(i);
        }

        var themeDict = new ResourceDictionary
        {
            Source = theme == "Light"
                ? new Uri("Themes/LightTheme.xaml", UriKind.Relative)
                : new Uri("Themes/DarkTheme.xaml", UriKind.Relative)
        };
        dicts.Add(themeDict);

        if (saveToSettings && changed)
        {
            try
            {
                AppSettings.Default.Theme = theme;
                AppSettings.Default.Save();
            }
            catch
            {
                // read-only профиль — тема применена, но не запомнится
            }
        }
    }

    private static string LoadThemeFromSettings()
    {
        try
        {
            string t = AppSettings.Default.Theme;
            return (t == "Light" || t == "Dark") ? t : "Dark";
        }
        catch
        {
            return "Dark";
        }
    }
}