using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SecureChat.Client.Models;
using SecureChat.Client.Services;
using SecureChat.Client.View;
using SecureChat.Core;
using Serilog;

namespace SecureChat.Client;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand ExitCommand = new(
        "Выход", nameof(ExitCommand), typeof(MainWindow));
    public static readonly RoutedUICommand ToggleThemeCommand = new(
        "Сменить тему", nameof(ToggleThemeCommand), typeof(MainWindow));
    public static readonly RoutedUICommand ChangeNameCommand = new(
        "Изменить имя", nameof(ChangeNameCommand), typeof(MainWindow));

    private static readonly ILogger Log = Serilog.Log.ForContext<MainWindow>();

    private readonly ObservableCollection<ContactItem> _contacts = new();
    private readonly Dictionary<int, ObservableCollection<DialogMessage>> _allDialogs = new();
    private ICollectionView _contactsView;

    private readonly ObservableCollection<ChannelItem> _channels = new();
    private readonly ChannelCrypto _channelCrypto = new();
    private readonly Dictionary<string, ObservableCollection<DialogMessage>> _allChannelMessages = new();
    private readonly Dictionary<string, Dictionary<int, string>> _channelMemberNames = new();
    private readonly HashSet<string> _loadedChannelIds = new();
    private readonly Dictionary<int, bool> _directHasMore = new();
    private readonly Dictionary<int, string> _oldestDirectTimestamp = new();
    private readonly Dictionary<string, bool> _channelHasMore = new();
    private readonly Dictionary<string, string> _oldestChannelTimestamp = new();
    private ChannelItem? _currentChannel;

    private NetworkService _network;
    private CryptoService _crypto = new();
    private DialogManager _dialogManager;
    private FileTransferManager _fileTransfer;

    private int _clientId;
    private string _userName;
    private string _token;
    private string _serverAddress;
    private string _publicKeyPem;
    private string _privateKeyPem;
    private string _role;

    private int _currentContactId = -1;
    private Dictionary<int, string> _contactPublicKeys = new();

    // Reply (Фаза D): цель ответа — применяется к текущему диалогу/каналу
    private string? _replyToMessageId;

    public MainWindow()
    {
        InitializeComponent();
        _userName = "User";
        _token = "";
        _role = "Employee";
        _serverAddress = LoginWindow.StaticServerUrl;
        _publicKeyPem = "";
        _privateKeyPem = "";
        _network = new NetworkService();
        _dialogManager = new DialogManager(_crypto);
        _fileTransfer = new FileTransferManager(_crypto, _network);
        InitCommon();
    }

    public MainWindow(int clientId, string userName, string token, string serverAddress, string publicKeyPem, string privateKeyPem, string role, NetworkService network)
    {
        InitializeComponent();
        _clientId = clientId;
        _userName = userName;
        _token = token;
        _serverAddress = serverAddress;
        _publicKeyPem = publicKeyPem;
        _privateKeyPem = privateKeyPem;
        _role = string.IsNullOrEmpty(role) ? "Employee" : role;
        _network = network;
        _dialogManager = new DialogManager(_crypto);
        _fileTransfer = new FileTransferManager(_crypto, _network);

        InitCommon();

        _network.OnIncomingMessage += OnIncomingMessage;
        _network.OnDeliveryStatus += OnDeliveryStatus;
        _network.OnConnected += () => Dispatcher.Invoke(() => UpdateConnectionStatus(true));
        _network.OnDisconnected += () => Dispatcher.Invoke(() => UpdateConnectionStatus(false));
        _network.OnError += err =>
        {
            Log.Error("Network error: {Error}", err);
            Dispatcher.Invoke(() => MessageBox.Show(MapErrorText(err), "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error));
        };
        _network.OnBanned += reason => Dispatcher.Invoke(() => _ = OnBannedAsync(reason));
        _network.OnGroupKeyDelivery += OnGroupKeyDelivery;
        _network.OnChannelKeyRotated += p => Dispatcher.Invoke(() => { /* состояние сбросится при доставке нового ключа */ });
        _network.OnChannelMemberAdded += p => Dispatcher.Invoke(() => _ = RefreshChannelsAsync());
        _network.OnChannelMemberRemoved += p => Dispatcher.Invoke(() => _ = OnChannelMemberRemovedAsync(p));
        _network.OnIncomingChannelMessage += OnIncomingChannelMessage;
        _network.OnUserProfileUpdated += OnUserProfileUpdated;
        _network.OnUserInfoResponse += OnUserInfoResponse;
        _network.OnContactList += OnContactList;
        _network.OnAddContactResponse += OnAddContactResponse;

        _network.OnUserOnline += userId =>
        {
            Dispatcher.Invoke(() =>
            {
                var contact = _contacts.FirstOrDefault(c => c.Id == userId);
                if (contact != null) contact.IsOnline = true;
            });
        };
        _network.OnUserOffline += userId =>
        {
            Dispatcher.Invoke(() =>
            {
                var contact = _contacts.FirstOrDefault(c => c.Id == userId);
                if (contact != null) contact.IsOnline = false;
            });
        };

        UpdateTitleAndStatus();
        Log.Information("MainWindow opened userId={UserId}", _clientId);

        this.Loaded += async (s, e) => { await _network.GetContactsAsync(); };
    }

    /// <summary>Нейтральные тексты ошибок сервера: без стектрейсов и внутренних деталей (требование 1.4.3).</summary>
    private static string MapErrorText(string errorCode) => errorCode switch
    {
        "MessageRejected" => "Сообщение отклонено сервером: проверьте размер и формат.",
        "InvalidUserName" => "Некорректное имя пользователя (1–100 символов).",
        "UnknownClient" => "Сессия недействительна. Выполните вход заново.",
        _ => $"Ошибка: {errorCode}"
    };

    private void InitCommon()
    {
        CommandBindings.Add(new CommandBinding(ExitCommand, (s, e) => Application.Current.Shutdown()));
        CommandBindings.Add(new CommandBinding(ToggleThemeCommand, (s, e) => ToggleTheme_Click(s, e)));
        CommandBindings.Add(new CommandBinding(ChangeNameCommand, (s, e) => ChangeName_Click(s, e)));
        _contactsView = CollectionViewSource.GetDefaultView(_contacts);
        ContactsListBox.ItemsSource = _contactsView;
    }

    private void SearchAndAddTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchAndAddTextBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) _contactsView.Filter = null;
        else
        {
            _contactsView.Filter = item =>
            {
                if (item is ContactItem contact)
                {
                    return (!string.IsNullOrEmpty(contact.RawName) && contact.RawName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        || contact.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || contact.Id.ToString() == query;
                }
                return false;
            };
        }
        _contactsView.Refresh();
    }

    private async void SearchAndAddTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        string input = SearchAndAddTextBox.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;

        if (int.TryParse(input, out int id) && id > 0)
        {
            if (id == _clientId)
            {
                MessageBox.Show("Нельзя начать диалог с самим собой.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var existingContact = _contacts.FirstOrDefault(c => c.Id == id);
            if (existingContact != null)
            {
                SearchAndAddTextBox.Clear();
                _contactsView.Filter = null;
                ContactsListBox.SelectedItem = existingContact;
                return;
            }

            var newContact = new ContactItem { Id = id, RawName = $"User {id}", IsOnline = false };
            _contacts.Add(newContact);
            if (!_allDialogs.ContainsKey(id)) _allDialogs[id] = new ObservableCollection<DialogMessage>();

            await _network.AddContactAsync(new AddContactPayload { ContactId = id });
            await _network.GetUserInfoAsync(new GetUserInfoPayload { TargetId = id });

            SearchAndAddTextBox.Clear();
            _contactsView.Filter = null;
            ContactsListBox.SelectedItem = newContact;
        }
        else
        {
            var matchingContacts = _contactsView.Cast<ContactItem>().ToList();
            if (matchingContacts.Count == 1) ContactsListBox.SelectedItem = matchingContacts[0];
        }
    }

    private string? GetContactPublicKey(int contactId)
    {
        _contactPublicKeys.TryGetValue(contactId, out var key);
        return key;
    }

    private void SetContactPublicKey(int contactId, string key) => _contactPublicKeys[contactId] = key;

    private async Task<string?> EnsurePublicKeyAsync(int contactId)
    {
        var cached = GetContactPublicKey(contactId);
        if (!string.IsNullOrEmpty(cached)) return cached;

        var tcs = new TaskCompletionSource<string>();
        void handler(PublicKeyResponsePayload p)
        {
            if (p.TargetId == contactId && p.Status == "Ok") tcs.TrySetResult(p.PublicKey);
        }
        _network.OnPublicKeyResponse += handler;
        try
        {
            await _network.GetPublicKeyAsync(new GetPublicKeyPayload { TargetId = contactId });
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            if (completed != tcs.Task) return null;
            var key = await tcs.Task;
            SetContactPublicKey(contactId, key);
            return key;
        }
        finally
        {
            _network.OnPublicKeyResponse -= handler;
        }
    }

    private void UpdateTitleAndStatus()
    {
        Title = $"SecureChat Enterprise - {_userName} (ID: {_clientId})";
        UserIdTextBlock.Text = $"{_userName} (ID: {_clientId})";
        ConnectionIndicator.Fill = _network.IsConnected ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Gray);
        AdminMenu.Visibility = _role == "Admin" ? Visibility.Visible : Visibility.Collapsed;
        bool hrOrAdmin = _role is "Admin" or "Manager";
        ChannelCreateMenuItem.Visibility = hrOrAdmin ? Visibility.Visible : Visibility.Collapsed;
        ChannelAddMemberMenuItem.Visibility = hrOrAdmin ? Visibility.Visible : Visibility.Collapsed;
        ChannelRemoveMemberMenuItem.Visibility = hrOrAdmin ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateConnectionStatus(bool connected)
    {
        ConnectionIndicator.Fill = connected ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Gray);
        UserIdTextBlock.Text = connected ? $"{_userName} (ID: {_clientId})" : $"{_userName} (ID: {_clientId}) (отключено)";
    }

    // ---------- Блокировка (событие Banned от сервера) ----------

    private async Task OnBannedAsync(string reason)
    {
        MessageBox.Show(
            string.IsNullOrEmpty(reason)
                ? "Ваш аккаунт заблокирован администратором."
                : $"Ваш аккаунт заблокирован администратором.\nПричина: {reason}",
            "Блокировка", MessageBoxButton.OK, MessageBoxImage.Warning);
        await PerformAccountSwitchAsync();
    }

    private async void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        await PerformAccountSwitchAsync();
    }

    private async Task PerformAccountSwitchAsync()
    {
        await _network.DisconnectAsync(logout: true);
        LoginWindow.ClearAutoLoginState();
        var app = (App)Application.Current;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Hide();

        try
        {
            var loginWindow = new LoginWindow();
            bool? res = loginWindow.ShowDialog();
            if (res == true && loginWindow.AuthenticatedUserId > 0)
            {
                var nextMain = new MainWindow(
                    loginWindow.AuthenticatedUserId, loginWindow.AuthenticatedUserName,
                    loginWindow.Token, loginWindow.ServerUrl,
                    loginWindow.PublicKeyPem, loginWindow.PrivateKeyPem,
                    loginWindow.AuthenticatedUserRole, loginWindow.Network);
                Application.Current.MainWindow = nextMain;
                nextMain.Show();
                Close();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
            else
            {
                app.Shutdown();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Account switch failed");
            app.Shutdown();
            throw;
        }
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _network.ConnectAsync(_serverAddress);
            await _network.HelloAsync(new HelloPayload { ClientId = _clientId, Token = _token });
            await _network.GetContactsAsync();
            UpdateTitleAndStatus();
            MessageBox.Show("Переподключение выполнено успешно.", "Сервер", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reconnect failed");
            MessageBox.Show($"Reconnect failed: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Администрирование (видно только Admin) ----------

    private async void BanSelectedUser_Click(object sender, RoutedEventArgs e)
    {
        if (ContactsListBox.SelectedItem is not ContactItem contact)
        {
            MessageBox.Show("Выберите пользователя в списке контактов.", "Администрирование",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? reason = ShowInputDialog("Причина блокировки", "");
        if (reason == null) return;

        var resp = await _network.BanUserAsync(new BanUserPayload { TargetId = contact.Id, Reason = reason });
        if (resp?.Status == "Ok")
            MessageBox.Show($"Пользователь {contact.Name} (ID: {contact.Id}) заблокирован.",
                "Администрирование", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show($"Не удалось заблокировать: {MapAdminError(resp?.Status)}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void UnbanSelectedUser_Click(object sender, RoutedEventArgs e)
    {
        if (ContactsListBox.SelectedItem is not ContactItem contact)
        {
            MessageBox.Show("Выберите пользователя в списке контактов.", "Администрирование",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var resp = await _network.UnbanUserAsync(new UnbanUserPayload { TargetId = contact.Id });
        if (resp?.Status == "Ok")
            MessageBox.Show($"Пользователь {contact.Name} (ID: {contact.Id}) разблокирован.",
                "Администрирование", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show($"Не удалось разблокировать: {MapAdminError(resp?.Status)}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void ChangeRoleSelectedUser_Click(object sender, RoutedEventArgs e)
    {
        if (ContactsListBox.SelectedItem is not ContactItem contact)
        {
            MessageBox.Show("Выберите пользователя в списке контактов.", "Администрирование",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? role = ShowInputDialog("Роль: Admin / Менеджер / Employee", "Employee");
        if (string.IsNullOrWhiteSpace(role)) return;
        // «Менеджер» — русскоязычный ввод роли Manager
        string normalized = role switch
        {
            "Менеджер" => "Manager",
            _ => role
        };
        if (normalized is not ("Admin" or "Manager" or "Employee"))
        {
            MessageBox.Show("Недопустимая роль. Доступны: Admin, Менеджер, Employee.",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var resp = await _network.ChangeRoleAsync(new ChangeRolePayload { TargetId = contact.Id, Role = normalized });
        if (resp?.Status == "Ok")
            MessageBox.Show($"Роль {contact.Name} (ID: {contact.Id}) изменена на {normalized}.",
                "Администрирование", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show($"Не удалось сменить роль: {MapAdminError(resp?.Status)}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void AuditLog_Click(object sender, RoutedEventArgs e)
    {
        var window = new AuditLogWindow(_network) { Owner = this };
        window.Show();
    }

    /// <summary>Нейтральные тексты результатов админ-операций.</summary>
    private static string MapAdminError(string? status) => status switch
    {
        "Forbidden" => "Недостаточно прав.",
        "NotFound" => "Пользователь не найден.",
        "CannotBanSelf" => "Нельзя заблокировать себя.",
        "CannotChangeOwnRole" => "Нельзя изменить собственную роль.",
        "InvalidReason" => "Некорректная причина блокировки.",
        "InvalidRole" => "Недопустимая роль.",
        "WrongPassword" => "Текущий пароль неверен.",
        "WeakPassword" => "Пароль: минимум 8 символов, хотя бы одна буква и одна цифра.",
        "NotConnected" => "Нет подключения к серверу.",
        _ => "ошибка сервера."
    };

    // ---------- Смена пароля ----------

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PasswordDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var resp = await _network.ChangePasswordAsync(new ChangePasswordPayload
        {
            CurrentPassword = dlg.CurrentPassword,
            NewPassword = dlg.NewPassword
        });

        if (resp?.Status == "Ok")
        {
            MessageBox.Show("Пароль изменён. Все сессии отозваны — выполните вход заново.",
                "Смена пароля", MessageBoxButton.OK, MessageBoxImage.Information);
            await PerformAccountSwitchAsync();
        }
        else
        {
            MessageBox.Show($"Не удалось сменить пароль: {MapAdminError(resp?.Status)}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Профиль: статус, аватар, просмотр ----------

    private async void ChangeStatus_Click(object sender, RoutedEventArgs e)
    {
        string? status = ShowInputDialog("Статус (до 100 символов)", "");
        if (status == null) return;

        var resp = await _network.UpdateStatusAsync(new UpdateStatusPayload { Status = status });
        if (resp?.Status != "Ok")
            MessageBox.Show("Статус отклонён: максимум 100 символов, без управляющих символов.",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void UploadAvatar_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите изображение аватара",
            Filter = "Изображения (PNG, JPEG)|*.png;*.jpg;*.jpeg|Все файлы|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            byte[] png = AvatarEncoder.EncodeToPng(dlg.FileName);
            var resp = await _network.UploadAvatarAsync(new UploadAvatarPayload
            {
                DataBase64 = Convert.ToBase64String(png)
            });
            if (resp?.Status == "Ok")
                MessageBox.Show("Аватар обновлён.", "Профиль", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show($"Аватар отклонён: {MapAdminError(resp?.Status)}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ShowMyProfile_Click(object sender, RoutedEventArgs e)
    {
        await ShowProfileAsync(_clientId);
    }

    private async void ShowSelectedProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ContactsListBox.SelectedItem is not ContactItem contact)
        {
            MessageBox.Show("Выберите пользователя в списке контактов.", "Профиль",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await ShowProfileAsync(contact.Id);
    }

    private async Task ShowProfileAsync(int targetId)
    {
        var profile = await _network.GetUserProfileAsync(new GetUserProfilePayload { TargetId = targetId });
        if (profile == null)
        {
            MessageBox.Show("Профиль недоступен: пользователь не найден среди ваших контактов.",
                "Профиль", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new ProfileWindow(profile) { Owner = this };
        window.Show();
    }

    // ---------- Каналы: список, создание, участники, ротация ключа (Фаза C) ----------

    private async void ListModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool channelsMode = ListModeComboBox.SelectedIndex == 1;
        ContactsListBox.Visibility = channelsMode ? Visibility.Collapsed : Visibility.Visible;
        ChannelsListBox.Visibility = channelsMode ? Visibility.Visible : Visibility.Collapsed;
        if (channelsMode) await RefreshChannelsAsync();
    }

    private async Task RefreshChannelsAsync()
    {
        var list = await _network.GetChannelsAsync();
        if (list == null) return;
        _channels.Clear();
        foreach (var c in list.Channels)
        {
            _channels.Add(new ChannelItem
            {
                Id = c.Id,
                Name = c.Name,
                OwnerId = c.OwnerId,
                IsOwner = c.OwnerId == _clientId,
                Subtitle = $"{c.MembersCount} участн. · владелец: {c.OwnerName}"
            });
        }
    }

    private async void RefreshChannels_Click(object sender, RoutedEventArgs e)
    {
        await RefreshChannelsAsync();
        ChannelsListBox.SelectedItem = _currentChannel;
    }

    private async void CreateChannel_Click(object sender, RoutedEventArgs e)
    {
        string? name = ShowInputDialog("Название канала (1–100 символов)", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        var resp = await _network.CreateChannelAsync(new CreateChannelPayload { Name = name });
        if (resp?.Status == "Ok")
        {
            // Owner генерирует GroupKey локально; доставки другим не нужны (участников пока нет)
            _channelCrypto.SetGroupKey(resp.ChannelId, _crypto.GenerateSessionKey());
            await RefreshChannelsAsync();
            ListModeComboBox.SelectedIndex = 1;
        }
        else
        {
            MessageBox.Show($"Не удалось создать канал: {MapChannelError(resp?.Status)}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddChannelMember_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelsListBox.SelectedItem is not ChannelItem channel)
        {
            MessageBox.Show("Выберите канал в списке (режим «Каналы»).", "Каналы",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(ShowInputDialog("ID пользователя для добавления", ""), out int userId) || userId <= 0)
            return;

        var resp = await _network.AddChannelMemberAsync(new AddChannelMemberPayload
        { ChannelId = channel.Id, UserId = userId });
        if (resp?.Status != "Ok")
        {
            MessageBox.Show($"Не удалось добавить участника: {MapChannelError(resp?.Status)}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // backward secrecy: новый ключ доставляется всем, включая нового участника
        await RotateChannelKeyAsync(channel.Id);
        await RefreshChannelsAsync();
    }

    /// <summary>Канал, ротация которого уже выполнена вручную (чтобы не сработать дважды на то же событие).</summary>
    private string? _rotationDoneForRemovedEvent;

    private async Task OnChannelMemberRemovedAsync(ChannelMemberRemovedPayload p)
    {
        var channel = _channels.FirstOrDefault(c => c.Id == p.ChannelId);
        await RefreshChannelsAsync();

        // forward secrecy при бане/удалении: Owner канала ротирует ключ (требование 1.2.7).
        // При ручном удалении ротацию уже выполнил действующий HR/Admin — пропускаем.
        if (channel != null && channel.OwnerId == _clientId &&
            _rotationDoneForRemovedEvent != p.ChannelId)
        {
            await RotateChannelKeyAsync(p.ChannelId);
        }
        if (_rotationDoneForRemovedEvent == p.ChannelId)
            _rotationDoneForRemovedEvent = null;
    }

    private async void RemoveChannelMember_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelsListBox.SelectedItem is not ChannelItem channel)
        {
            MessageBox.Show("Выберите канал в списке (режим «Каналы»).", "Каналы",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(ShowInputDialog("ID пользователя для удаления", ""), out int userId) || userId <= 0)
            return;

        var resp = await _network.RemoveChannelMemberAsync(new RemoveChannelMemberPayload
        { ChannelId = channel.Id, UserId = userId });
        if (resp?.Status != "Ok")
        {
            MessageBox.Show($"Не удалось удалить участника: {MapChannelError(resp?.Status)}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // forward secrecy: новый ключ — только оставшимся участникам;
        // пришедшее следом событие ChannelMemberRemoved пропускаем, чтобы не ротировать дважды
        _rotationDoneForRemovedEvent = channel.Id;
        await RotateChannelKeyAsync(channel.Id);
        await RefreshChannelsAsync();
    }

    /// <summary>
    /// Ротация GroupKey: новый CSPRNG-ключ, доставка каждому участнику через 1-1 E2E
    /// (RSA + существующий EncryptedKey-механизм), анонс ChannelKeyRotated в группу.
    /// Сервер видит только зашифрованные копии ключа.
    /// </summary>
    private async Task RotateChannelKeyAsync(string channelId)
    {
        byte[] newKey = _crypto.GenerateSessionKey();
        long version = _channelCrypto.SetGroupKey(channelId, newKey);

        var members = await _network.GetChannelMembersAsync(new JoinChannelPayload { ChannelId = channelId });
        if (members == null) return;

        foreach (var member in members.Members)
        {
            if (member.UserId == _clientId) continue; // свой ключ уже установлен
            string? pubKey = await EnsurePublicKeyAsync(member.UserId);
            if (pubKey == null)
            {
                Log.Warning("Skip key delivery: no public key userId={UserId} channelId={ChannelId}",
                    member.UserId, channelId);
                continue;
            }
            byte[] encKey = _crypto.RsaEncrypt(pubKey, newKey);
            await _network.SendGroupKeyDeliveryAsync(new GroupKeyDeliveryPayload
            {
                ChannelId = channelId,
                TargetId = member.UserId,
                Version = version,
                EncryptedKey = Convert.ToBase64String(encKey)
            });
        }

        await _network.SendChannelKeyRotatedAsync(new ChannelKeyRotatedPayload
        { ChannelId = channelId, Version = version });
        Log.Information("GroupKey rotated channelId={ChannelId} version={Version} members={Count}",
            channelId, version, members.Members.Count);
    }

    /// <summary>Приём GroupKey: расшифровка своим приватным ключом, сброс ratchet-цепочек.</summary>
    private void OnGroupKeyDelivery(GroupKeyDeliveryPayload payload)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                byte[] encKey = Convert.FromBase64String(payload.EncryptedKey);
                byte[] groupKey = _crypto.RsaDecrypt(_privateKeyPem, encKey);
                _channelCrypto.SetGroupKey(payload.ChannelId, groupKey, payload.Version);
                Log.Information("GroupKey accepted channelId={ChannelId} version={Version}",
                    payload.ChannelId, payload.Version);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to accept GroupKey channelId={ChannelId}", payload.ChannelId);
            }
        });
    }

    // ---------- Каналы: переписка и история ----------

    private async void ChannelsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelsListBox.SelectedItem is not ChannelItem channel) return;
        _currentChannel = channel;
        _currentContactId = -1;

        if (!_allChannelMessages.ContainsKey(channel.Id))
            _allChannelMessages[channel.Id] = new ObservableCollection<DialogMessage>();
        MessagesListBox.ItemsSource = _allChannelMessages[channel.Id];

        await CacheChannelMemberNamesAsync(channel.Id);
        await EnsureChannelHistoryAsync(channel.Id);
    }

    private async Task CacheChannelMemberNamesAsync(string channelId)
    {
        if (_channelMemberNames.ContainsKey(channelId)) return;
        var resp = await _network.GetChannelMembersAsync(new JoinChannelPayload { ChannelId = channelId });
        if (resp == null) return;
        _channelMemberNames[channelId] = resp.Members.ToDictionary(m => m.UserId, m => m.UserName);
    }

    private string GetChannelMemberName(string channelId, int userId) =>
        _channelMemberNames.TryGetValue(channelId, out var names) && names.TryGetValue(userId, out var name)
            ? name
            : $"User {userId}";

    private async Task EnsureChannelHistoryAsync(string channelId)
    {
        if (_loadedChannelIds.Contains(channelId)) return;
        _loadedChannelIds.Add(channelId);
        await LoadChannelHistoryPageAsync(channelId, loadMore: false);
    }

    /// <summary>Страница истории канала; loadMore — «Загрузить ещё» (prepend старых сообщений).</summary>
    private async Task LoadChannelHistoryPageAsync(string channelId, bool loadMore)
    {
        string? before = loadMore && _oldestChannelTimestamp.TryGetValue(channelId, out var t) ? t : null;
        var resp = await _network.GetChannelHistoryAsync(new GetChannelHistoryPayload
        { ChannelId = channelId, BeforeTimestamp = before, Limit = 50 });
        if (resp == null) return;

        _channelHasMore[channelId] = resp.HasMore;
        if (resp.Messages.Count > 0)
            _oldestChannelTimestamp[channelId] = resp.Messages[0].Timestamp;

        var messages = resp.Messages;
        messages.Reverse(); // сервер отдаёт по убыванию времени; расшифровка — по возрастанию

        foreach (var m in messages)
        {
            if (m.ContentType == "File")
            {
                // в истории файл отображается именем (авто-загрузка — только для живых сообщений)
                AddChannelMessage(channelId, new DialogMessage
                {
                    Id = m.MessageId,
                    SenderId = m.SenderId,
                    FilePath = m.FileName ?? "file",
                    ContentType = "File",
                    Status = "",
                    IsOutgoing = m.SenderId == _clientId,
                    SenderName = m.SenderId == _clientId ? "" : GetChannelMemberName(channelId, m.SenderId),
                    Timestamp = DateTime.Parse(m.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                }, prepend: loadMore);
                continue;
            }
            if (m.ContentType != "Text" || string.IsNullOrEmpty(m.Ciphertext)) continue;
            byte[]? key = _channelCrypto.DeriveMessageKeyAt(channelId, m.SenderId, m.ChainIteration);
            if (key == null)
            {
                Log.Warning("History skipped: no GroupKey channelId={ChannelId} messageId={MessageId}", channelId, m.MessageId);
                continue;
            }
            try
            {
                byte[] plain = _crypto.AesGcmDecrypt(key,
                    Convert.FromBase64String(m.Nonce ?? ""),
                    Convert.FromBase64String(m.Ciphertext),
                    Convert.FromBase64String(m.Tag ?? ""));

                AddChannelMessage(channelId, new DialogMessage
                {
                    Id = m.MessageId,
                    SenderId = m.SenderId,
                    Text = Encoding.UTF8.GetString(plain),
                    ContentType = "Text",
                    Status = "",
                    IsOutgoing = m.SenderId == _clientId,
                    SenderName = m.SenderId == _clientId ? "" : GetChannelMemberName(channelId, m.SenderId),
                    Timestamp = DateTime.Parse(m.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                }, prepend: loadMore);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "History decrypt failed channelId={ChannelId} messageId={MessageId}", channelId, m.MessageId);
            }
        }
    }

    private void AddChannelMessage(string channelId, DialogMessage msg, bool prepend = false)
    {
        if (!_allChannelMessages.ContainsKey(channelId))
            _allChannelMessages[channelId] = new ObservableCollection<DialogMessage>();
        if (_allChannelMessages[channelId].Any(m => m.Id == msg.Id)) return;
        if (prepend) _allChannelMessages[channelId].Insert(0, msg);
        else _allChannelMessages[channelId].Add(msg);
        if (_currentChannel?.Id == channelId)
            MessagesListBox.ItemsSource = _allChannelMessages[channelId];
    }

    private void OnIncomingChannelMessage(IncomingChannelMessagePayload msg)
    {
        Dispatcher.Invoke(() =>
        {
            byte[]? key = _channelCrypto.GetMessageKey(msg.ChannelId, msg.SenderId, msg.ChainIteration);
            if (key == null)
            {
                Log.Warning("Channel message dropped (no key or replay) channelId={ChannelId} senderId={SenderId} iteration={Iteration}",
                    msg.ChannelId, msg.SenderId, msg.ChainIteration);
                return;
            }
            if (msg.ContentType == "File")
            {
                ReceiveChannelFile(msg, key);
                return;
            }
            if (msg.ContentType != "Text" || msg.EncryptedData == null) return;

            try
            {
                byte[] plain = _crypto.AesGcmDecrypt(key,
                    Convert.FromBase64String(msg.EncryptedData.Nonce),
                    Convert.FromBase64String(msg.EncryptedData.Ciphertext),
                    Convert.FromBase64String(msg.EncryptedData.Tag));

                var dm = new DialogMessage
                {
                    Id = msg.MessageId,
                    SenderId = msg.SenderId,
                    Text = Encoding.UTF8.GetString(plain),
                    ContentType = "Text",
                    Status = "",
                    IsOutgoing = msg.SenderId == _clientId,
                    SenderName = msg.SenderId == _clientId ? "" : GetChannelMemberName(msg.ChannelId, msg.SenderId),
                    Timestamp = DateTime.Parse(msg.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    ReplyToMessageId = msg.ReplyToMessageId
                };
                if (_allChannelMessages.TryGetValue(msg.ChannelId, out var chanCol))
                    ApplyReplyPreview(dm, chanCol);
                AddChannelMessage(msg.ChannelId, dm);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Channel message decrypt failed channelId={ChannelId} messageId={MessageId}",
                    msg.ChannelId, msg.MessageId);
            }
        });
    }

    /// <summary>Отправка сообщения в канал: MessageKey из ratchet, AES-GCM, ChainIteration.</summary>
    private async Task SendChannelMessageTextAsync(string channelId, string text)
    {
        var next = _channelCrypto.GetNextMessageKey(channelId, _clientId);
        if (next == null)
        {
            MessageBox.Show("Ключ канала ещё не получен — дождитесь доставки от владельца.",
                "Каналы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var (messageKey, iteration) = next.Value;

        byte[] plain = Encoding.UTF8.GetBytes(text);
        var (ct, nonce, tag) = _crypto.AesGcmEncrypt(messageKey, plain);
        string msgId = Guid.NewGuid().ToString();

        var outgoing = new DialogMessage
        {
            Id = msgId,
            SenderId = _clientId,
            Text = text,
            ContentType = "Text",
            Status = "",
            IsOutgoing = true,
            Timestamp = DateTime.Now,
            ReplyToMessageId = _replyToMessageId
        };
        if (_allChannelMessages.TryGetValue(channelId, out var replyCol))
            ApplyReplyPreview(outgoing, replyCol);
        AddChannelMessage(channelId, outgoing);

        await _network.SendChannelMessageAsync(new SendChannelMessagePayload
        {
            ChannelId = channelId,
            MessageId = msgId,
            ChainIteration = iteration,
            EncryptedData = new EncryptedData
            {
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(ct),
                Tag = Convert.ToBase64String(tag)
            },
            ContentType = "Text",
            ReplyToMessageId = _replyToMessageId
        });

        Log.Information("Channel message sent channelId={ChannelId} length={Length} iteration={Iteration} messageId={MessageId}",
            channelId, text.Length, iteration, msgId);

        MessageTextBox.Clear();
        ClearReply();
    }

    /// <summary>Отправка файла в канал: ≤10 МБ, шифрование MessageKey из ratchet.</summary>
    private async Task SendChannelFileAsync()
    {
        if (_currentChannel == null)
        {
            MessageBox.Show("Выберите канал", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var next = _channelCrypto.GetNextMessageKey(_currentChannel.Id, _clientId);
        if (next == null)
        {
            MessageBox.Show("Ключ канала ещё не получен — дождитесь доставки от владельца.",
                "Каналы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog();
        if (dlg.ShowDialog() != true) return;

        var file = new FileInfo(dlg.FileName);
        if (file.Length > 10 * 1024 * 1024)
        {
            MessageBox.Show("Файл больше 10 МБ.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            var (messageKey, iteration) = next.Value;
            byte[] fileData = await File.ReadAllBytesAsync(dlg.FileName);
            var (ct, nonce, tag) = _crypto.AesGcmEncrypt(messageKey, fileData);
            string msgId = Guid.NewGuid().ToString();
            string fileName = Path.GetFileName(dlg.FileName);

            AddChannelMessage(_currentChannel.Id, new DialogMessage
            {
                Id = msgId,
                SenderId = _clientId,
                FilePath = fileName,
                ContentType = "File",
                Status = "",
                IsOutgoing = true,
                Timestamp = DateTime.Now
            });

            await _network.SendChannelMessageAsync(new SendChannelMessagePayload
            {
                ChannelId = _currentChannel.Id,
                MessageId = msgId,
                ChainIteration = iteration,
                EncryptedData = new EncryptedData
                {
                    Nonce = Convert.ToBase64String(nonce),
                    Ciphertext = Convert.ToBase64String(ct),
                    Tag = Convert.ToBase64String(tag)
                },
                ContentType = "File",
                FileName = fileName,
                FileSize = fileData.Length,
                FileTransferId = Guid.NewGuid().ToString()
            });

            Log.Information("Channel file sent channelId={ChannelId} {FileName} ({Size}B) iteration={Iteration} messageId={MessageId}",
                _currentChannel.Id, fileName, fileData.Length, iteration, msgId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send channel file channelId={ChannelId}", _currentChannel.Id);
            MessageBox.Show($"Не удалось отправить файл: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Приём файла из канала: расшифровка и сохранение в Downloads.</summary>
    private void ReceiveChannelFile(IncomingChannelMessagePayload msg, byte[] messageKey)
    {
        try
        {
            byte[] plain = _crypto.AesGcmDecrypt(messageKey,
                Convert.FromBase64String(msg.EncryptedData!.Nonce),
                Convert.FromBase64String(msg.EncryptedData.Ciphertext),
                Convert.FromBase64String(msg.EncryptedData.Tag));

            string downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads\\";
            Directory.CreateDirectory(downloads);
            string path = Path.Combine(downloads, msg.FileName ?? "received_file");
            File.WriteAllBytes(path, plain);

            AddChannelMessage(msg.ChannelId, new DialogMessage
            {
                Id = msg.MessageId,
                SenderId = msg.SenderId,
                FilePath = path,
                ContentType = "File",
                Status = "",
                IsOutgoing = false,
                SenderName = GetChannelMemberName(msg.ChannelId, msg.SenderId),
                Timestamp = DateTime.Parse(msg.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            });

            Log.Information("Channel file received {FileName} ({Size}B) from userId={SenderId} channelId={ChannelId}",
                msg.FileName, plain.Length, msg.SenderId, msg.ChannelId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Channel file decrypt failed channelId={ChannelId} messageId={MessageId}",
                msg.ChannelId, msg.MessageId);
        }
    }

    // ---------- Reply (Фаза D) ----------

    /// <summary>Цитата строится локально: сервер не видит plaintext (E2E).</summary>
    private void ApplyReplyPreview(DialogMessage dm, IEnumerable<DialogMessage> collection)
    {
        if (string.IsNullOrEmpty(dm.ReplyToMessageId)) return;
        var original = collection.FirstOrDefault(m => m.Id == dm.ReplyToMessageId);
        if (original == null)
        {
            dm.ReplyPreview = "↩ ответ на сообщение";
            return;
        }
        string author = original.IsOutgoing ? _userName : ResolveSenderName(original);
        string text = original.ContentType == "Text" ? original.Text : (original.FilePath ?? "файл");
        if (text.Length > 100) text = text[..100] + "…";
        dm.ReplyPreview = $"↩ {author}: {text}";
    }

    private string ResolveSenderName(DialogMessage msg)
    {
        if (ListModeComboBox.SelectedIndex == 1 && _currentChannel != null)
            return GetChannelMemberName(_currentChannel.Id, msg.SenderId);
        var contact = _contacts.FirstOrDefault(c => c.Id == msg.SenderId);
        return contact != null ? contact.RawName : $"User {msg.SenderId}";
    }

    private void StartReply(DialogMessage msg)
    {
        _replyToMessageId = msg.Id;
        string author = msg.IsOutgoing ? _userName : ResolveSenderName(msg);
        string preview = msg.ContentType == "Text" ? msg.Text : (msg.FilePath ?? "файл");
        if (preview.Length > 100) preview = preview[..100] + "…";
        ReplyBarText.Text = $"Ответ на: {author}: {preview}";
        ReplyBar.Visibility = Visibility.Visible;
        MessageTextBox.Focus();
    }

    private void ReplyToMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm &&
            cm.PlacementTarget is ListBoxItem item && item.DataContext is DialogMessage msg)
            StartReply(msg);
    }

    private void CancelReply_Click(object sender, RoutedEventArgs e)
    {
        ClearReply();
    }

    private void ClearReply()
    {
        _replyToMessageId = null;
        ReplyBar.Visibility = Visibility.Collapsed;
    }

    private static string MapChannelError(string? status) => status switch
    {
        "Forbidden" => "Недостаточно прав (нужна роль HR или Admin).",
        "NotFound" => "Канал или пользователь не найден.",
        "InvalidName" => "Название: 1–100 символов, без управляющих символов.",
        "NameExists" => "Канал с таким названием уже существует.",
        "AlreadyMember" => "Пользователь уже участник канала.",
        "CannotRemoveOwner" => "Нельзя удалить владельца канала.",
        "CannotRemoveSelf" => "Нельзя удалить себя.",
        "NotConnected" => "Нет подключения к серверу.",
        _ => "ошибка сервера."
    };

    private void ShowMyPublicKey_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_publicKeyPem);
        MessageBox.Show("Публичный ключ скопирован в буфер обмена.", "Ключ", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("SecureChat Enterprise v2.0\n\nКорпоративный мессенджер на WebSocket со сквозным шифрованием (RSA-2048 + AES-256-GCM).",
            "О программе", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void HelpGuide_Click(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow();
        help.Owner = this;
        help.Show();
    }

    private void OnUserProfileUpdated(UserProfileUpdatedPayload update)
    {
        Dispatcher.Invoke(() =>
        {
            var contact = _contacts.FirstOrDefault(c => c.Id == update.UserId);
            if (contact != null) contact.RawName = update.UserName;
        });
    }

    private void OnUserInfoResponse(UserInfoResponsePayload info)
    {
        Dispatcher.Invoke(() =>
        {
            if (info.Status == "Ok")
            {
                var contact = _contacts.FirstOrDefault(c => c.Id == info.UserId);
                if (contact != null) contact.RawName = info.UserName;
            }
        });
    }

    /// <summary>
    /// Загрузка страницы Direct-истории (курсорная пагинация).
    /// loadMore=false — первая страница при открытии диалога; true — «Загрузить ещё» (prepend).
    /// </summary>
    private async Task LoadDirectHistoryAsync(int contactId, bool loadMore)
    {
        string? before = loadMore && _oldestDirectTimestamp.TryGetValue(contactId, out var t) ? t : null;
        var resp = await _network.GetHistoryAsync(new GetHistoryRequestPayload
        { ContactId = contactId, BeforeTimestamp = before, Limit = 50 });
        if (resp == null) return;

        _directHasMore[contactId] = resp.HasMore;
        if (resp.Messages.Count > 0)
            _oldestDirectTimestamp[contactId] = resp.Messages[0].Timestamp;

        Dispatcher.Invoke(() =>
        {
            if (!_allDialogs.ContainsKey(contactId))
                _allDialogs[contactId] = new ObservableCollection<DialogMessage>();
            var dialog = _allDialogs[contactId];
            var existingIds = new HashSet<string>(dialog.Select(m => m.Id));

            foreach (var hm in resp.Messages)
            {
                if (existingIds.Contains(hm.MessageId)) continue;

                byte[]? sessionKey = null;
                if (hm.SenderId == _clientId)
                {
                    sessionKey = _dialogManager.GetSessionKey(contactId);
                    if (sessionKey == null && hm.EncryptedKeySelf != null)
                    {
                        byte[] encKeySelf = Convert.FromBase64String(hm.EncryptedKeySelf);
                        byte[] decKey = _crypto.RsaDecrypt(_privateKeyPem, encKeySelf);
                        _dialogManager.SetSessionKey(contactId, decKey);
                        sessionKey = decKey;
                    }
                }
                else if (hm.SenderId == contactId)
                {
                    if (hm.EncryptedKey != null)
                    {
                        byte[] encKey = Convert.FromBase64String(hm.EncryptedKey);
                        byte[] decKey = _crypto.RsaDecrypt(_privateKeyPem, encKey);
                        _dialogManager.SetSessionKey(contactId, decKey);
                        sessionKey = decKey;
                    }
                    else
                    {
                        sessionKey = _dialogManager.GetSessionKey(contactId);
                    }
                }

                if (sessionKey == null) continue;

                DialogMessage dm;
                if (hm.ContentType == "Text" && hm.Ciphertext != null && hm.Nonce != null && hm.Tag != null)
                {
                    byte[] ct = Convert.FromBase64String(hm.Ciphertext);
                    byte[] n = Convert.FromBase64String(hm.Nonce);
                    byte[] t = Convert.FromBase64String(hm.Tag);
                    byte[] plain = _crypto.AesGcmDecrypt(sessionKey, n, ct, t);
                    dm = new DialogMessage
                    {
                        Id = hm.MessageId,
                        SenderId = hm.SenderId,
                        ReceiverId = hm.ReceiverId,
                        Text = Encoding.UTF8.GetString(plain),
                        ContentType = "Text",
                        Status = hm.Status,
                        IsOutgoing = hm.SenderId == _clientId,
                        Timestamp = DateTime.Parse(hm.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    };
                }
                else if (hm.ContentType == "File")
                {
                    dm = new DialogMessage
                    {
                        Id = hm.MessageId,
                        SenderId = hm.SenderId,
                        ReceiverId = hm.ReceiverId,
                        FilePath = hm.FileName ?? "file",
                        ContentType = "File",
                        Status = hm.Status,
                        IsOutgoing = hm.SenderId == _clientId,
                        Timestamp = DateTime.Parse(hm.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    };
                }
                else continue;

                if (hm.IsDeleted)
                {
                    dm.Text = "Сообщение удалено";
                    dm.ContentType = "Text";
                }
                dm.IsEdited = hm.IsEdited;
                dm.ReplyToMessageId = hm.ReplyToMessageId;
                ApplyReplyPreview(dm, dialog);

                if (loadMore) dialog.Insert(0, dm); // «ещё» — старые сообщения сверху
                else dialog.Add(dm);
                existingIds.Add(hm.MessageId);
            }

            if (_currentContactId == contactId)
            {
                MessagesListBox.ItemsSource = dialog;
                UpdateLoadMoreButton();
            }
        });
    }

    private void UpdateLoadMoreButton()
    {
        bool hasMore = ListModeComboBox.SelectedIndex == 1
            ? (_currentChannel != null && _channelHasMore.TryGetValue(_currentChannel.Id, out var ch) && ch)
            : (_currentContactId > 0 && _directHasMore.TryGetValue(_currentContactId, out var dh) && dh);
        LoadMoreButton.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (ListModeComboBox.SelectedIndex == 1)
        {
            if (_currentChannel == null) return;
            await LoadChannelHistoryPageAsync(_currentChannel.Id, loadMore: true);
        }
        else if (_currentContactId > 0)
        {
            await LoadDirectHistoryAsync(_currentContactId, loadMore: true);
        }
        UpdateLoadMoreButton();
    }

    private void OnContactList(ContactListPayload list)
    {
        Dispatcher.Invoke(() =>
        {
            _contacts.Clear();
            foreach (var user in list.Contacts)
            {
                if (!_allDialogs.ContainsKey(user.Id))
                    _allDialogs[user.Id] = new ObservableCollection<DialogMessage>();
                _contacts.Add(new ContactItem { Id = user.Id, RawName = user.Name, IsOnline = user.IsOnline });
            }
            if (_contacts.Count > 0 && _currentContactId == -1)
                ContactsListBox.SelectedIndex = 0;
            Log.Debug("ContactList received count={Count}", list.Contacts.Count);
        });
    }

    private void OnDeliveryStatus(DeliveryStatusPayload status)
    {
        Dispatcher.Invoke(() =>
        {
            Log.Debug("DeliveryStatus messageId={MessageId} status={Status}", status.MessageId, status.Status);
            var msg = _allDialogs.Values.SelectMany(c => c).FirstOrDefault(m => m.Id == status.MessageId);
            if (msg != null)
            {
                msg.Status = status.Status switch
                {
                    "Sent" => "✓",
                    "Delivered" => "✓✓",
                    "UserOffline" => "✗ офлайн",
                    _ => status.Status
                };
                MessagesListBox.Items.Refresh();
            }
        });
    }

    private void OnIncomingMessage(IncomingMessagePayload msg)
    {
        Dispatcher.Invoke(() =>
        {
            Log.Information("Message received from userId={SenderId} type={ContentType} size={Size}B messageId={MessageId}",
                msg.SenderId, msg.ContentType, msg.FileSize, msg.MessageId);

            byte[]? sessionKey = _dialogManager.GetSessionKey(msg.SenderId);
            if (msg.EncryptedKey != null)
            {
                byte[] encKey = Convert.FromBase64String(msg.EncryptedKey);
                byte[] decKey = _crypto.RsaDecrypt(_privateKeyPem, encKey);
                _dialogManager.SetSessionKey(msg.SenderId, decKey);
                sessionKey = decKey;
            }
            if (sessionKey == null) return;

            if (msg.ContentType == "Text" && msg.EncryptedData != null)
            {
                byte[] ct = Convert.FromBase64String(msg.EncryptedData.Ciphertext);
                byte[] n = Convert.FromBase64String(msg.EncryptedData.Nonce);
                byte[] t = Convert.FromBase64String(msg.EncryptedData.Tag);
                byte[] plain = _crypto.AesGcmDecrypt(sessionKey, n, ct, t);

                var incoming = new DialogMessage
                {
                    Id = msg.MessageId,
                    SenderId = msg.SenderId,
                    ReceiverId = _clientId,
                    Text = Encoding.UTF8.GetString(plain),
                    ContentType = "Text",
                    Status = "✓✓",
                    IsOutgoing = false,
                    Timestamp = DateTime.Now,
                    ReplyToMessageId = msg.ReplyToMessageId
                };
                if (_allDialogs.TryGetValue(msg.SenderId, out var col))
                    ApplyReplyPreview(incoming, col);
                AddMessage(msg.SenderId, incoming);

                _ = _network.AckAsync(new AckPayload { MessageId = msg.MessageId, Status = "Delivered" });
            }
            else if (msg.ContentType == "File")
            {
                var senderId = msg.SenderId;
                var messageId = msg.MessageId;
                var fileMsg = msg;
                var key = sessionKey;

                _ = Task.Run(() =>
                {
                    try
                    {
                        _fileTransfer.HandleIncomingFile(fileMsg, key, path =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                AddMessage(senderId, new DialogMessage
                                {
                                    Id = messageId,
                                    SenderId = senderId,
                                    ReceiverId = _clientId,
                                    FilePath = path,
                                    ContentType = "File",
                                    Status = "✓✓",
                                    IsOutgoing = false,
                                    Timestamp = DateTime.Now
                                });
                                _ = _network.AckAsync(new AckPayload { MessageId = messageId, Status = "Delivered" });
                            });
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "File receive error messageId={MessageId}", messageId);
                        Dispatcher.Invoke(() => MessageBox.Show(
                            $"Ошибка приёма файла: {ex.Message}",
                            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error));
                    }
                });
            }
        });
    }

    private void OnAddContactResponse(AddContactResponsePayload response)
    {
        Dispatcher.Invoke(() =>
        {
            if (response.Status == "NotFound")
            {
                MessageBox.Show($"Сотрудник с ID {response.ContactId} не найден на сервере.",
                                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                var contact = _contacts.FirstOrDefault(c => c.Id == response.ContactId);
                if (contact != null)
                {
                    _contacts.Remove(contact);
                    if (_allDialogs.ContainsKey(response.ContactId)) _allDialogs.Remove(response.ContactId);
                    if (_currentContactId == response.ContactId)
                    {
                        _currentContactId = -1;
                        MessagesListBox.ItemsSource = null;
                    }
                }
            }
        });
    }

    private void AddMessage(int contactId, DialogMessage msg)
    {
        if (!_allDialogs.ContainsKey(contactId))
        {
            _allDialogs[contactId] = new ObservableCollection<DialogMessage>();
            if (_contacts.All(c => c.Id != contactId))
                _contacts.Add(new ContactItem { Id = contactId, RawName = $"User {contactId}", IsOnline = true });
        }
        if (_allDialogs[contactId].Any(m => m.Id == msg.Id)) return;
        _allDialogs[contactId].Add(msg);
        if (_currentContactId == contactId)
            MessagesListBox.ItemsSource = _allDialogs[contactId];
    }

    private async void ContactsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContactsListBox.SelectedItem is ContactItem contact)
        {
            _currentChannel = null;
            _currentContactId = contact.Id;
            if (!_allDialogs.ContainsKey(_currentContactId))
                _allDialogs[_currentContactId] = new ObservableCollection<DialogMessage>();
            MessagesListBox.ItemsSource = _allDialogs[_currentContactId];

            if (_allDialogs[_currentContactId].Count == 0)
                await LoadDirectHistoryAsync(contact.Id, loadMore: false);
            UpdateLoadMoreButton();
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        // режим «Каналы»: сообщение уходит в канал через групповой ключ
        if (ListModeComboBox.SelectedIndex == 1)
        {
            if (_currentChannel == null)
            {
                MessageBox.Show("Выберите канал", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string channelText = MessageTextBox.Text.Trim();
            if (string.IsNullOrEmpty(channelText)) return;
            if (channelText.Length > 4000)
            {
                MessageBox.Show("Сообщение слишком длинное: максимум 4000 символов.",
                    "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await SendChannelMessageTextAsync(_currentChannel.Id, channelText);
            MessageTextBox.Clear();
            return;
        }

        if (_currentContactId <= 0) { MessageBox.Show("Выберите получателя", "Информация", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        // NFC-нормализация до шифрования (требование 1.4.2)
        string text = TextNormalizer.NormalizeToNfc(MessageTextBox.Text.Trim());
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > 4000)
        {
            MessageBox.Show("Сообщение слишком длинное: максимум 4000 символов.",
                "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            string? pubKey = await EnsurePublicKeyAsync(_currentContactId);
            if (pubKey == null)
            {
                MessageBox.Show("Не удалось получить открытый ключ собеседника.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            byte[] sessionKey = _dialogManager.GetOrCreateSessionKey(_currentContactId);
            byte[] encKey = _crypto.RsaEncrypt(pubKey, sessionKey);
            byte[] encKeySelf = _crypto.RsaEncrypt(_publicKeyPem, sessionKey);
            byte[] plain = Encoding.UTF8.GetBytes(text);
            var (ct, nonce, tag) = _crypto.AesGcmEncrypt(sessionKey, plain);
            var msgId = Guid.NewGuid().ToString();

            var outgoing = new DialogMessage
            {
                Id = msgId,
                SenderId = _clientId,
                ReceiverId = _currentContactId,
                Text = text,
                ContentType = "Text",
                Status = "Отправка...",
                IsOutgoing = true,
                Timestamp = DateTime.Now,
                ReplyToMessageId = _replyToMessageId
            };
            if (_allDialogs.TryGetValue(_currentContactId, out var replyCol))
                ApplyReplyPreview(outgoing, replyCol);
            AddMessage(_currentContactId, outgoing);

            await _network.SendMessageAsync(new SendMessagePayload
            {
                ReceiverId = _currentContactId,
                MessageId = msgId,
                EncryptedKey = Convert.ToBase64String(encKey),
                EncryptedKeySelf = Convert.ToBase64String(encKeySelf),
                EncryptedData = new EncryptedData
                {
                    Nonce = Convert.ToBase64String(nonce),
                    Ciphertext = Convert.ToBase64String(ct),
                    Tag = Convert.ToBase64String(tag)
                },
                ContentType = "Text",
                ReplyToMessageId = _replyToMessageId
            });

            Log.Information("Message sent to userId={ReceiverId} length={Length} messageId={MessageId}",
                _currentContactId, text.Length, msgId);

            MessageTextBox.Clear();
            ClearReply();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send message contactId={ContactId}", _currentContactId);
            MessageBox.Show($"Не удалось отправить сообщение: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AttachFileButton_Click(object sender, RoutedEventArgs e)
    {
        // режим «Каналы»: файл шифруется MessageKey из ratchet и уходит в канал
        if (ListModeComboBox.SelectedIndex == 1)
        {
            await SendChannelFileAsync();
            return;
        }

        if (_currentContactId <= 0) { MessageBox.Show("Выберите получателя", "Информация", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            string? pubKey = await EnsurePublicKeyAsync(_currentContactId);
            if (pubKey == null)
            {
                MessageBox.Show("Таймаут получения открытого ключа.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog();
            if (dlg.ShowDialog() == true)
            {
                byte[] sessionKey = _dialogManager.GetOrCreateSessionKey(_currentContactId);
                byte[] encKey = _crypto.RsaEncrypt(pubKey, sessionKey);
                byte[] encKeySelf = _crypto.RsaEncrypt(_publicKeyPem, sessionKey);

                string msgId = await _fileTransfer.SendFileAsync(
                    _currentContactId, dlg.FileName, sessionKey,
                    Convert.ToBase64String(encKey), Convert.ToBase64String(encKeySelf));

                AddMessage(_currentContactId, new DialogMessage
                {
                    Id = msgId,
                    SenderId = _clientId,
                    ReceiverId = _currentContactId,
                    FilePath = Path.GetFileName(dlg.FileName),
                    ContentType = "File",
                    Status = "Отправка...",
                    IsOutgoing = true,
                    Timestamp = DateTime.Now
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send file contactId={ContactId}", _currentContactId);
            MessageBox.Show($"Не удалось отправить файл: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MessageTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SendButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        string newTheme = App.CurrentTheme == "Dark" ? "Light" : "Dark";
        App.ApplyTheme(newTheme);
        Log.Information("Theme changed to {Theme}", newTheme);
    }

    private async void ChangeName_Click(object sender, RoutedEventArgs e)
    {
        string? newName = ShowInputDialog("Имя пользователя", _userName);
        if (!string.IsNullOrWhiteSpace(newName) && newName != _userName)
        {
            _userName = newName.Trim();
            UpdateTitleAndStatus();
            await _network.UpdateProfileAsync(new UpdateProfilePayload { UserName = _userName });
            Log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} newName={NewName}",
                "ProfileUpdate", _clientId, _userName);
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        Log.Information("Closing MainWindow userId={UserId}", _clientId);
        _ = _network.DisconnectAsync(logout: true);
    }

    private string? ShowInputDialog(string title, string defaultText)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 320,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = this.Background,
            Foreground = this.Foreground
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var textBox = new TextBox { Text = defaultText, Margin = new Thickness(8), VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetRow(textBox, 0);
        grid.Children.Add(textBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        var okBtn = new Button { Content = "OK", Width = 70, Height = 24, Margin = new Thickness(4, 0, 4, 0) };
        var cancelBtn = new Button { Content = "Отмена", Width = 70, Height = 24, Margin = new Thickness(4, 0, 4, 0) };

        bool? result = null;
        okBtn.Click += (_, _) => { result = true; dialog.Close(); };
        cancelBtn.Click += (_, _) => { result = false; dialog.Close(); };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        Grid.SetRow(btnPanel, 1);
        grid.Children.Add(btnPanel);

        dialog.Content = grid;
        dialog.ShowDialog();
        return result == true ? textBox.Text : null;
    }
}