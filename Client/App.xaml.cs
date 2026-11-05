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

namespace SecureChat.Client;

public partial class App : Application
{
    public static string CurrentTheme { get; set; } = "Dark";
    /// <summary>Текущая локаль в полном формате BCP-47/ISO: "ru-RU" | "en-US" (требование 1.8.1).</summary>
    public static string CurrentLocale { get; set; } = "ru-RU";
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

    /// <summary>
    /// Смена локали на лету: принимается полный код ("ru-RU", "en-US") или короткий ("ru", "en").
    /// CurrentCulture — формат дат/чисел; CurrentUICulture — выбор строк ресурсов.
    /// </summary>
    public static void ApplyLanguage(string language)
    {
        string locale = NormalizeLocale(language);
        CurrentLocale = locale;

        var culture = new CultureInfo(locale);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        LocalizationSource.Instance.Refresh();
        PersistLanguage(locale);
        Log.Information("Locale switched to {Locale}", locale);
    }

    /// <summary>Короткий код → полная локаль (язык + регион, ISO 639 + ISO 3166).</summary>
    public static string NormalizeLocale(string language)
    {
        switch (language)
        {
            case "ru": case "ru-RU": case "ru_RU": return "ru-RU";
            case "en": case "en-US": case "en_US": return "en-US";
            default: return "ru-RU";
        }
    }

    private static void ApplyLanguageFromConfig()
    {
        string saved = ReadLanguageFromConfig();
        CurrentLocale = saved;
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
                if (cfg != null && cfg.TryGetValue("Language", out var lang))
                    return NormalizeLocale(lang); // совместимость со старыми "ru"/"en"
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read language preference");
        }
        // по умолчанию — локаль системы
        var os = CultureInfo.InstalledUICulture;
        return os.TwoLetterISOLanguageName == "ru" ? "ru-RU" : "en-US";
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