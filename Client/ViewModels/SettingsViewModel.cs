using CommunityToolkit.Mvvm.ComponentModel;

namespace SecureChat.Client.ViewModels;

/// <summary>
/// ViewModel настроек (Лаба 8): язык (RU/EN, переключение на лету) и тема.
/// Применение — через статические App.ApplyLanguage/App.ApplyTheme.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private int _selectedLanguageIndex;
    [ObservableProperty] private int _selectedThemeIndex;

    public SettingsViewModel()
    {
        _selectedLanguageIndex = App.CurrentLocale == "en-US" ? 1 : 0;
        _selectedThemeIndex = App.CurrentTheme == "Light" ? 1 : 0;
    }

    /// <summary>Применяет выбранные язык и тему (только если изменились).</summary>
    public void Apply()
    {
        string lang = SelectedLanguageIndex == 1 ? "en-US" : "ru-RU";
        if (lang != App.CurrentLocale) App.ApplyLanguage(lang);

        string theme = SelectedThemeIndex == 1 ? "Light" : "Dark";
        if (theme != App.CurrentTheme) App.ApplyTheme(theme);
    }
}
