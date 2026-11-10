using System.ComponentModel;

namespace SecureChat.Client.Models;

public class ContactSelectionItem : INotifyPropertyChanged
{
    private bool _isSelected;
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string RawName { get; set; } = "";
    public bool IsOnline { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
