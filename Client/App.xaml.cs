using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Serilog;
using SecureChat.Client.Services;
using SecureChat.Client.View;
using AppSettings = SecureChat.Client.Properties.Settings;

namespace SecureChat.Client;

public partial class App : Application
{
    public static string CurrentTheme { get; set; } = "Dark";
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