using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SecureChat.Client.Models;

namespace SecureChat.Client.View;

public partial class ChannelCreationWindow : Window
{
    private readonly ObservableCollection<ContactSelectionItem> _allItems;
    private ICollectionView _itemsView;

    public string ChannelName => ChannelNameTextBox.Text.Trim();
    public List<int> SelectedMemberIds => _allItems.Where(i => i.IsSelected).Select(i => i.Id).ToList();

    public ChannelCreationWindow(IEnumerable<ContactItem> contacts)
    {
        InitializeComponent();
        _allItems = new ObservableCollection<ContactSelectionItem>(
            contacts.Select(c => new ContactSelectionItem
            {
                Id = c.Id,
                Name = c.Name,
                RawName = c.RawName,
                IsOnline = c.IsOnline,
                IsSelected = false
            })
        );
        _itemsView = CollectionViewSource.GetDefaultView(_allItems);
        ParticipantsListBox.ItemsSource = _itemsView;
        ChannelNameTextBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            _itemsView.Filter = null;
        }
        else
        {
            _itemsView.Filter = item =>
            {
                if (item is ContactSelectionItem ci)
                {
                    return (!string.IsNullOrEmpty(ci.RawName) && ci.RawName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        || ci.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || ci.Id.ToString() == query;
                }
                return false;
            };
        }
        _itemsView.Refresh();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ChannelName))
        {
            MessageBox.Show("Введите название канала.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            ChannelNameTextBox.Focus();
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
