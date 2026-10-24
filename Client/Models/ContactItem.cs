using System.ComponentModel;

namespace SecureChat.Client.Models;

// Элемент списка контактов с уведомлением UI об изменениях (INotifyPropertyChanged)
public class ContactItem : INotifyPropertyChanged
{
    private string _name = "";                       // «Сырое» имя без ID
    private bool _isOnline;                          // Статус присутствия
    private string _statusText = "не в сети";        // Текст статуса для UI

    public int Id { get; set; }

    // Отображаемое имя в формате "Имя (ID: N)"
    public string Name
    {
        get => $"{_name} (ID: {Id})";
        set
        {
            _name = value;
            OnPropertyChanged(nameof(Name));
        }
    }

    // Имя без ID (для внутреннего использования / обновления)
    public string RawName
    {
        get => _name;
        set
        {
            _name = value;
            OnPropertyChanged(nameof(Name));      // Name зависит от _name
            OnPropertyChanged(nameof(RawName));
        }
    }

    // Онлайн-статус: при изменении обновляет и StatusText
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            _isOnline = value;
            OnPropertyChanged(nameof(IsOnline));
            StatusText = _isOnline ? "в сети" : "не в сети";
        }
    }

    // Текст статуса для отображения
    public string StatusText
    {
        get => _statusText;
        set
        {
            _statusText = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    // Уведомление UI об изменении свойства
    protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}