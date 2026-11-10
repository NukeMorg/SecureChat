using System.Windows;
using SecureChat.Client.ViewModels;
using SecureChat.Core;

namespace SecureChat.Client.View;

/// <summary>
/// Представление профиля пользователя; данные — в <see cref="ProfileViewModel"/>.
/// </summary>
public partial class ProfileWindow : Window
{
    public ProfileWindow(UserProfileDto profile)
    {
        InitializeComponent();
        DataContext = new ProfileViewModel(profile);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
