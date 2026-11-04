using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Extensions.Configuration;
using Serilog;
using SecureChat.Client.Services;
using SecureChat.Client.View;
using AppSettings = SecureChat.Client.Properties.Settings;
using Application = System.Windows.Application;

namespace SecureChat.Client;

public partial class App : Application
{
    public static string CurrentTheme { get; set; } = "Dark";
    public static string CurrentLanguage { get; set; } = "ru";
    public static NetworkService Network { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .CreateLogger();

        // Локализация (требование 1.8): CurrentCulture — формат данных, CurrentUICulture — строки UI.
        // Язык UI переключается на лету; формат логов от языка не зависит (всегда English/Invariant).
        ApplyLanguageFromConfig();
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(
                XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag)));

        Log.Information("=== SecureChat Client started v2.0 on {Machine} ===", Environment.MachineName);
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Fatal(ex.ExceptionObject as Exception, "UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, ex) =>
            Log.Error(ex.Exception, "UnobservedTaskException");

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        CurrentTheme = LoadThemeFromSettings();
        ApplyThemeInternal(CurrentTheme, saveToSettings: false);

        var saved = LoginWindow.ReadAutoLoginConfig();
        if (saved != null)
        {
            Log.Information("Auto-login attempt userId={UserId}", saved.UserId);
            var session = LoginWindow.TryAutoLoginBlocking(Network, saved, timeoutMs: 5000);
            if (session != null)
            {
                var mainWindow = new MainWindow(session.UserId, session.UserName, session.Token,
                    LoginWindow.StaticServerUrl, session.PublicKey, session.PrivateKey, session.Role, Network);
                MainWindow = mainWindow;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                mainWindow.Show();
                return;
            }
            Log.Warning("Auto-login failed, showing login window");
        }

        var loginWindow = new LoginWindow(Network);
        bool? result = loginWindow.ShowDialog();

        if (result == true && loginWindow.AuthenticatedUserId > 0)
        {
            var mainWindow = new MainWindow(loginWindow.AuthenticatedUserId, loginWindow.AuthenticatedUserName,
                loginWindow.Token, loginWindow.ServerUrl, loginWindow.PublicKeyPem, loginWindow.PrivateKeyPem,
                loginWindow.AuthenticatedUserRole, Network);
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        else
        {
            Log.Information("Login cancelled - shutting down");
            Network.Disconnect();
            Log.CloseAndFlush();
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("=== SecureChat Client shutdown ===");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    public static void ApplyTheme(string theme) => ApplyThemeInternal(theme, saveToSettings: true);

    // ---------- Локализация (Лаба 8) ----------

    /// <summary>Смена языка на лету: культуры потока + обновление всех Loc-привязок.</summary>
    public static void ApplyLanguage(string language)
    {
        if (language != "ru" && language != "en") language = "ru";
        CurrentLanguage = language;

        var culture = new CultureInfo(language);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        LocalizationSource.Instance.Refresh();
        PersistLanguage(language);
        Log.Information("Language switched to {Language}", language);
    }

    private static void ApplyLanguageFromConfig()
    {
        string saved = ReadLanguageFromConfig();
        CurrentLanguage = saved;
        var culture = new CultureInfo(saved);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    private static string ReadLanguageFromConfig()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SecureChat", "config.json");
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (cfg != null && cfg.TryGetValue("Language", out var lang) && (lang == "ru" || lang == "en"))
                    return lang;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read language preference");
        }
        // по умолчанию — язык системы
        return CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
    }

    private static void PersistLanguage(string language)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecureChat");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "config.json");
            var cfg = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new Dictionary<string, string>()
                : new Dictionary<string, string>();
            cfg["Language"] = language;
            File.WriteAllText(path, JsonSerializer.Serialize(cfg));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist language");
        }
    }

    private static void ApplyThemeInternal(string theme, bool saveToSettings)
    {
        if (theme != "Dark" && theme != "Light") theme = "Dark";
        bool changed = CurrentTheme != theme;
        CurrentTheme = theme;

        var dicts = Current.Resources.MergedDictionaries;
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Theme.xaml")) dicts.RemoveAt(i);
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
            try { AppSettings.Default.Theme = theme; AppSettings.Default.Save(); }
            catch (Exception ex) { Log.Warning(ex, "Failed to save theme"); }
        }
    }

    private static string LoadThemeFromSettings()
    {
        try
        {
            string t = AppSettings.Default.Theme;
            return (t == "Light" || t == "Dark") ? t : "Dark";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read theme, using Dark");
            return "Dark";
        }
    }
}