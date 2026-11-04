using System.Windows;
using System.Windows.Input;
using SecureChat.Client.Services;

namespace SecureChat.Client.View;

/// <summary>
/// Клиентский поиск по расшифрованным сообщениям (требование 1.2.3): минимум 3 символа,
/// не более 100 результатов. Поиск выполняет MainWindow по загруженным диалогам и каналам.
/// </summary>
public record SearchResultItem(string Source, bool IsChannel, int ContactId, string ChannelId,
    string MessageId, string Time, string Text);

public partial class SearchWindow : Window
{
    private readonly Func<string, List<SearchResultItem>> _search;
    private readonly Action<SearchResultItem> _jump;

    public SearchWindow(Func<string, List<SearchResultItem>> search, Action<SearchResultItem> jump)
    {
        InitializeComponent();
        _search = search;
        _jump = jump;
        QueryBox.Focus();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => RunSearch();

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RunSearch();
    }

    private void RunSearch()
    {
        string query = QueryBox.Text.Trim();
        if (query.Length < 3)
        {
            HintTextBlock.Text = "Введите минимум 3 символа.";
            return;
        }

        var results = _search(query);
        ResultsList.ItemsSource = results;
        HintTextBlock.Text = $"Найдено: {results.Count} (максимум 100). Двойной клик — переход к диалогу.";
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is SearchResultItem result)
        {
            _jump(result);
            Close();
        }
    }
}
