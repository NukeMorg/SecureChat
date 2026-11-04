using System.ComponentModel;

namespace SecureChat.Client.Models;

/// <summary>
/// Элемент списка каналов (левая панель, режим «Каналы»).
/// </summary>
public class ChannelItem : INotifyPropertyChanged
{
    private string _name = "";
    private string _subtitle = "";

    public string Id { get; set; } = "";
    public int OwnerId { get; set; }
    public bool IsOwner { get; set; }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(nameof(Name)); }
    }

    public string Subtitle
    {
        get => _subtitle;
        set { _subtitle = value; OnPropertyChanged(nameof(Subtitle)); }
    }

    // Непрочитанные сообщения канала (Фаза D)
    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            _unreadCount = value;
            OnPropertyChanged(nameof(UnreadCount));
            OnPropertyChanged(nameof(UnreadBadge));
        }
    }

    public string UnreadBadge => _unreadCount > 0 ? $"● {_unreadCount}" : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
