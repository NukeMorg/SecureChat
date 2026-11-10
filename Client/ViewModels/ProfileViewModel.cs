using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SecureChat.Core;

namespace SecureChat.Client.ViewModels;

/// <summary>
/// ViewModel профиля пользователя: аватар (Base64 → ImageSource), имя, ID+роль,
/// онлайн-статус и статус.
/// </summary>
public class ProfileViewModel : ObservableObject
{
    public ImageSource? Avatar { get; }
    public string DisplayName { get; }
    public string IdRoleText { get; }
    public string OnlineText { get; }
    public string StatusText { get; }

    public ProfileViewModel(UserProfileDto profile)
    {
        DisplayName = profile.UserName;
        IdRoleText = $"ID: {profile.UserId} · Роль: {profile.Role}";
        OnlineText = profile.IsOnline ? "в сети" : "не в сети";
        StatusText = string.IsNullOrEmpty(profile.Status) ? "Статус не задан." : profile.Status;

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
                Avatar = image;
            }
            catch (FormatException)
            {
                // некорректные данные аватара — просто не показываем
            }
        }
    }
}
