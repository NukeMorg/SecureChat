using System.Windows;
using SecureChat.Client.ViewModels;

namespace SecureChat.Client
{
    // Окно справки с иерархическим деревом разделов; содержимое — в HelpViewModel.
    public partial class HelpWindow : Window
    {
        public HelpWindow()
        {
            InitializeComponent();
            var vm = new HelpViewModel();
            DataContext = vm;
            HelpTree.ItemsSource = vm.Topics;
        }
    }
}
