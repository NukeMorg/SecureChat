using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using SecureChat.Client.Services;
using SecureChat.Core;

namespace SecureChat.Client.View;

/// <summary>
/// Просмотр профиля пользователя: аватар, имя, ID, роль, онлайн-статус, статус.
/// </summary>
public partial class ProfileWindow : Window
{
    public ProfileWindow(UserProfileDto profile)
    {
        InitializeComponent();

        NameText.Text = profile.UserName;
        IdRoleText.Text = $"ID: {profile.UserId} · Роль: {RoleDisplay.Display(profile.Role)}";
        OnlineText.Text = profile.IsOnline ? "в сети" : "не в сети";
        StatusTextBlock.Text = string.IsNullOrEmpty(profile.Status) ? "Статус не задан." : profile.Status;

        if (!string.IsNullOrEmpty(profile.AvatarBase64))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(profile.AvatarBase64);
                var image = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                AvatarImage.Source = image;
            }
            catch (FormatException)
            {
                // некорректные данные аватара — просто не показываем
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
