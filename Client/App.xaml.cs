using SecureChat.Client.Properties;
using System.Linq;
using System.Windows;

namespace SecureChat.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyTheme(Settings.Default.Theme);
    }

    public static void ApplyTheme(string theme)
    {
        var dicts = Current.Resources.MergedDictionaries;
        // Удаляем все тематические словари (StubTheme и предыдущие)
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Theme.xaml"))
                dicts.RemoveAt(i);
        }

        var themeDict = new ResourceDictionary();
        if (theme == "Light")
            themeDict.Source = new Uri("Themes/LightTheme.xaml", UriKind.Relative);
        else
            themeDict.Source = new Uri("Themes/DarkTheme.xaml", UriKind.Relative);

        dicts.Add(themeDict);
    }
}