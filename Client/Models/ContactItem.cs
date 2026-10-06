using System.ComponentModel;

namespace SecureChat.Client.Models;

// Элемент списка контактов: ID, имя, онлайн-статус.
public class ContactItem : INotifyPropertyChanged
{
    private string _name;
    private bool _isOnline;
    private string _statusText = "";

    public int Id { get; set; }

    // Отображаемое имя + ID
    public string Name
    {
        get => $"{_name} (ID: {Id})";
        set
        {
            _name = value;
            OnPropertyChanged(nameof(Name));
        }
    }

    // Онлайн-статус
    public bool IsOnline
    {
        get => _isOnline;
        set { _isOnline = value; OnPropertyChanged(nameof(IsOnline)); OnPropertyChanged(nameof(StatusText)); }
    }

    // Текстовое представление статуса
    public string StatusText
    {
        get => _isOnline ? "в сети" : "не в сети";
        set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}