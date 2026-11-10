using System;
using System.Collections.Generic;
using System.Windows;
using SecureChat.Client.Models;
using SecureChat.Client.Services;
using SecureChat.Core;

namespace SecureChat.Client.View;

/// <summary>
/// Реализация <see cref="IWindowFactory"/> в слое представления: знает конкретные окна
/// и проставляет владельца (модальные/диалоговые окна центрируются по главному окну).
/// </summary>
public class WindowFactory : IWindowFactory
{
    /// <summary>Окна-диалоги получают главное окно владельцем.</summary>
    private static T Owned<T>(T window) where T : Window
    {
        window.Owner = Application.Current.MainWindow;
        return window;
    }

    public SettingsWindow CreateSettings() => Owned(new SettingsWindow());

    public AuditLogWindow CreateAuditLog(NetworkService network) => Owned(new AuditLogWindow(network));

    public ProfileWindow CreateProfile(UserProfileDto profile) => Owned(new ProfileWindow(profile));

    public ChannelCreationWindow CreateChannelCreation(IEnumerable<ContactItem> contacts) =>
        Owned(new ChannelCreationWindow(contacts));

    public PasswordDialog CreatePasswordDialog() => Owned(new PasswordDialog());

    public HelpWindow CreateHelp() => Owned(new HelpWindow());

    // окно входа и новое главное окно не получают владельца:
    // во время смены аккаунта текущее главное окно скрывается и закрывается
    public LoginWindow CreateLogin() => new LoginWindow();

    public MainWindow CreateMain(int clientId, string userName, string token, string serverAddress,
        string publicKeyPem, string privateKeyPem, string role, NetworkService network) =>
        new MainWindow(clientId, userName, token, serverAddress, publicKeyPem, privateKeyPem, role, network);
}
