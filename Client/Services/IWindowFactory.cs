using System.Collections.Generic;
using SecureChat.Client.Models;
using SecureChat.Client.View;
using SecureChat.Core;

namespace SecureChat.Client.Services;

/// <summary>
/// Фабрика окон (паттерн Factory): развязывает ViewModel от конкретных представлений —
/// окна создаёт реализация в слое View, ViewModel зависит только от интерфейса.
/// </summary>
public interface IWindowFactory
{
    SettingsWindow CreateSettings();
    AuditLogWindow CreateAuditLog(NetworkService network);
    ProfileWindow CreateProfile(UserProfileDto profile);
    ChannelCreationWindow CreateChannelCreation(IEnumerable<ContactItem> contacts);
    PasswordDialog CreatePasswordDialog();
    HelpWindow CreateHelp();
    LoginWindow CreateLogin();
    MainWindow CreateMain(int clientId, string userName, string token, string serverAddress,
        string publicKeyPem, string privateKeyPem, string role, NetworkService network);
}
