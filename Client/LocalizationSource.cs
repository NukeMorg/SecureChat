using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace SecureChat.Client;

/// <summary>
/// Источник локализованных строк (требование 1.8): ResourceManager по CurrentUICulture.
/// XAML: {Binding [KeyName], Source={StaticResource Loc}} — обновление на лету через Refresh().
/// Код: Strings.Get("KeyName").
/// </summary>
public class LocalizationSource : INotifyPropertyChanged
{
    /// <summary>Единственный экземпляр: XAML привязывается через {x:Static ...}, Refresh() уведомляет именно его.</summary>
    public static LocalizationSource Instance { get; } = new();

    private static readonly ResourceManager Rm =
        new("SecureChat.Client.Resources.Strings", typeof(LocalizationSource).Assembly);

    private LocalizationSource() { }

    public string this[string key] =>
        Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Строка для code-behind.</summary>
    public static string Get(string key) => Instance[key];

    /// <summary>Уведомить все привязки о смене языка (на лету, без перезапуска).</summary>
    public void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

    public event PropertyChangedEventHandler? PropertyChanged;
}
