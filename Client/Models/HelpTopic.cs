using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SecureChat.Client.Models
{
    // Тема справки: заголовок, текст, картинка, вложенные темы.
    public class HelpTopic : INotifyPropertyChanged
    {
        private string _title;
        private string _content;
        private string _imageSource;
        private List<HelpTopic> _children = new();

        public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }
        public string Content { get => _content; set { _content = value; OnPropertyChanged(); } }
        public string ImageSource { get => _imageSource; set { _imageSource = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasImage)); } }
        public List<HelpTopic> Children { get => _children; set { _children = value; OnPropertyChanged(); } }

        // Показывает, есть ли изображение
        public bool HasImage => !string.IsNullOrEmpty(ImageSource);

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}