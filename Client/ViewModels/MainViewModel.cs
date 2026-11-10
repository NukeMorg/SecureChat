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
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SecureChat.Client.Models;
using SecureChat.Client.Services;
using SecureChat.Core;
using Serilog;

namespace SecureChat.Client.ViewModels;

/// <summary>
/// ViewModel главного окна: состояние списка чатов и сообщений, пагинация истории,
/// отправка/приём E2E-сообщений (RSA + AES-GCM), групповая криптография каналов
/// (Sender Keys + ротация GroupKey), reply/edit/delete, поиск, администрирование,
/// профиль и экспорт. Представление держит только печать, диалоги файлов и фокус.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MainViewModel>();

    // ----- Состояние чатов -----
    private readonly ObservableCollection<ContactItem> _contacts = new();
    private readonly Dictionary<int, ObservableCollection<DialogMessage>> _allDialogs = new();
    private ICollectionView _contactsView = null!;

    private readonly ObservableCollection<ChannelItem> _channels = new();
    private readonly ChannelCrypto _channelCrypto = new();
    private readonly Dictionary<string, ObservableCollection<DialogMessage>> _allChannelMessages = new();
    private readonly Dictionary<string, Dictionary<int, string>> _channelMemberNames = new();
    private readonly HashSet<string> _loadedChannelIds = new();
    private readonly Dictionary<int, bool> _directHasMore = new();
    private readonly Dictionary<int, string> _oldestDirectTimestamp = new();
    private readonly Dictionary<string, bool> _channelHasMore = new();
    private readonly Dictionary<string, string> _oldestChannelTimestamp = new();

    private readonly NetworkService _network;
    private readonly CryptoService _crypto = new();
    private readonly DialogManager _dialogManager;
    private readonly FileTransferManager _fileTransfer;
    private readonly IDialogService _dialogs;

    // ----- Сессия -----
    private readonly int _clientId;
    private string _userName;
    private readonly string _token;
    private readonly string _serverAddress;
    private readonly string _publicKeyPem;
    private readonly string _privateKeyPem;
    private string _role;

    private int _currentContactId = -1;
    private Dictionary<int, string> _contactPublicKeys = new();

    // Reply: цель ответа — применяется к текущему диалогу/каналу
    private string? _replyToMessageId;

    // Edit: редактируемое сообщение (только Direct, окно 15 минут на сервере)
    private string? _editingMessageId;

    /// <summary>Канал, ротация которого уже выполнена вручную (чтобы не сработать дважды на то же событие).</summary>
    private string? _rotationDoneForRemovedEvent;

    // Поиск по выбранному чату
    private List<DialogMessage> _chatSearchMatches = new();
    private int _chatSearchIndex = -1;

    // ----- Привязываемое состояние UI -----
    [ObservableProperty] private string _title = "SecureChat Enterprise";
    [ObservableProperty] private string _headerUserText = "";
    [ObservableProperty] private Brush _connectionBrush = new SolidColorBrush(Colors.Gray);
    [ObservableProperty] private bool _isAdminVisible;
    [ObservableProperty] private bool _isHrVisible;

    /// <summary>Единый список чатов (Telegram-style): контакты + каналы.</summary>
    [ObservableProperty] private CompositeCollection _chatList = new();

    [ObservableProperty] private ObservableCollection<DialogMessage>? _currentMessages;
    [ObservableProperty] private object? _selectedChat;
    [ObservableProperty] private object? _selectedMessage;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _messageText = "";

    [ObservableProperty] private bool _isReplyBarVisible;
    [ObservableProperty] private string _replyBarText = "";
    [ObservableProperty] private bool _isLoadMoreVisible;
    [ObservableProperty] private bool _isChatSearchVisible;
    [ObservableProperty] private string _chatSearchQuery = "";
    [ObservableProperty] private string _chatSearchCount = "";

    /// <summary>Имя текущего пользователя (для печати в представлении).</summary>
    public string UserName => _userName;

    // События для представления: фокус поля ввода и прокрутка к найденному сообщению
    public event EventHandler? FocusMessageRequested;
    public event EventHandler? FocusSearchRequested;
    public event EventHandler<DialogMessage>? ScrollToMessageRequested;

    public MainViewModel(int clientId, string userName, string token, string serverAddress,
        string publicKeyPem, string privateKeyPem, string role, NetworkService network, IDialogService dialogs)
    {
        _clientId = clientId;
        _userName = userName;
        _token = token;
        _serverAddress = serverAddress;
        _publicKeyPem = publicKeyPem;
        _privateKeyPem = privateKeyPem;
        _role = string.IsNullOrEmpty(role) ? "Employee" : role;
        _network = network;
        _dialogs = dialogs;
        _dialogManager = new DialogManager(_crypto);
        _fileTransfer = new FileTransferManager(_crypto, _network);

        InitChatList();
        SubscribeNetworkEvents();
        UpdateTitleAndStatus();
        Log.Information("MainWindow opened userId={UserId}", _clientId);
    }

    /// <summary>Первая загрузка после показа окна: контакты и каналы.</summary>
    public async Task InitializeAsync()
    {
        await _network.GetContactsAsync();
        await RefreshChannelsAsync();
    }

    public async Task OnWindowClosingAsync()
    {
        Log.Information("Closing MainWindow userId={UserId}", _clientId);
        await _network.DisconnectAsync(logout: true);
    }

    // ---------- Инициализация списка чатов и подписки на сеть ----------

    private void InitChatList()
    {
        _contactsView = CollectionViewSource.GetDefaultView(_contacts);
        // единый список чатов (Telegram-style): контакты и каналы в одном ListBox
        ChatList = new CompositeCollection
        {
            new CollectionContainer { Collection = _contactsView },
            new CollectionContainer { Collection = _channels }
        };
    }

    private void SubscribeNetworkEvents()
    {
        _network.OnIncomingMessage += OnIncomingMessage;
        _network.OnDeliveryStatus += OnDeliveryStatus;
        _network.OnConnected += () => RunOnUi(async () =>
        {
            UpdateConnectionStatus(true);
            await RefreshChannelsAsync();
        });
        _network.OnDisconnected += () => RunOnUi(() => UpdateConnectionStatus(false));
        _network.OnError += err =>
        {
            Log.Error("Network error: {Error}", err);
            RunOnUi(() => _dialogs.ShowError(MapErrorText(err)));
        };
        _network.OnBanned += reason => RunOnUi(() => _ = OnBannedAsync(reason));
        _network.OnGroupKeyDelivery += OnGroupKeyDelivery;
        _network.OnChannelKeyRotated += p => RunOnUi(() => { /* состояние сбросится при доставке нового ключа */ });
        _network.OnChannelMemberAdded += p => RunOnUi(() => _ = RefreshChannelsAsync());
        _network.OnChannelMemberRemoved += p => RunOnUi(() => _ = OnChannelMemberRemovedAsync(p));
        _network.OnIncomingChannelMessage += OnIncomingChannelMessage;
        _network.OnMessageEdited += OnMessageEdited;
        _network.OnMessageDeleted += OnMessageDeleted;
        _network.OnUserProfileUpdated += OnUserProfileUpdated;
        _network.OnUserInfoResponse += OnUserInfoResponse;
        _network.OnContactList += OnContactList;
        _network.OnAddContactResponse += OnAddContactResponse;

        _network.OnUserOnline += userId =>
        {
            RunOnUi(() =>
            {
                var contact = _contacts.FirstOrDefault(c => c.Id == userId);
                if (contact != null) contact.IsOnline = true;
            });
        };
        _network.OnUserOffline += userId =>
        {
            RunOnUi(() =>
            {
                var contact = _contacts.FirstOrDefault(c => c.Id == userId);
                if (contact != null) contact.IsOnline = false;
            });
        };
    }

    /// <summary>События сети приходят из фоновых потоков — marshalling на поток UI.</summary>
    private static void RunOnUi(Action action) =>
        Application.Current.Dispatcher.Invoke(action);

    /// <summary>Нейтральные тексты ошибок сервера: без стектрейсов и внутренних деталей.</summary>
    private static string MapErrorText(string errorCode) => errorCode switch
    {
        "MessageRejected" => "Сообщение отклонено сервером: проверьте размер и формат.",
        "InvalidUserName" => "Некорректное имя пользователя (1–100 символов).",
        "UnknownClient" => "Сессия недействительна. Выполните вход заново.",
        _ => $"Ошибка: {errorCode}"
    };

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
        "EditWindowExpired" => "Срок редактирования истёк (15 минут).",
        "NotSupported" => "Редактирование доступно только в личных чатах.",
        "NotConnected" => "Нет подключения к серверу.",
        _ => "ошибка сервера."
    };

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

    /// <summary>Глифы статусов доставки: Sent → Delivered → Read.</summary>
    private static string MapStatusGlyph(string status) => status switch
    {
        "Sent" => "✓",
        "Delivered" => "✓✓",
        "Read" => "✓✓✓",
        "UserOffline" => "✗ офлайн",
        _ => status
    };

    /// <summary>Размер файла: число по CurrentCulture, единицы — из локализованных ресурсов.</summary>
    private static string FormatFileSize(long bytes) =>
        FileSizeFormatter.Format(bytes,
            LocalizationSource.Get("UnitBytes"),
            LocalizationSource.Get("UnitKilobytes"),
            LocalizationSource.Get("UnitMegabytes"));

    // ---------- Заголовок, статус подключения, роли ----------

    private void UpdateTitleAndStatus()
    {
        Title = $"SecureChat Enterprise - {_userName} (ID: {_clientId})";
        HeaderUserText = $"{_userName} (ID: {_clientId})";
        ConnectionBrush = _network.IsConnected ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Gray);
        IsAdminVisible = _role == "Admin";
        IsHrVisible = _role is "Admin" or "Manager";
    }

    private void UpdateConnectionStatus(bool connected)
    {
        ConnectionBrush = connected ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Gray);
        HeaderUserText = connected ? $"{_userName} (ID: {_clientId})" : $"{_userName} (ID: {_clientId}) (отключено)";
    }

    // ---------- Поиск/добавление контактов ----------

    partial void OnSearchQueryChanged(string value) => ApplyContactsFilter();

    private void ApplyContactsFilter()
    {
        string query = SearchQuery.Trim();
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

    [RelayCommand]
    private async Task AddContactFromSearchAsync()
    {
        string input = SearchQuery.Trim();
        if (string.IsNullOrEmpty(input)) return;

        if (int.TryParse(input, out int id) && id > 0)
        {
            if (id == _clientId)
            {
                _dialogs.ShowWarning("Нельзя начать диалог с самим собой.", "Ошибка");
                return;
            }
            var existingContact = _contacts.FirstOrDefault(c => c.Id == id);
            if (existingContact != null)
            {
                SearchQuery = "";
                SelectedChat = existingContact;
                return;
            }

            var newContact = new ContactItem { Id = id, RawName = $"User {id}", IsOnline = false };
            _contacts.Add(newContact);
            if (!_allDialogs.ContainsKey(id)) _allDialogs[id] = new ObservableCollection<DialogMessage>();

            await _network.AddContactAsync(new AddContactPayload { ContactId = id });
            await _network.GetUserInfoAsync(new GetUserInfoPayload { TargetId = id });

            SearchQuery = "";
            SelectedChat = newContact;
        }
        else
        {
            var matchingContacts = _contactsView.Cast<ContactItem>().ToList();
            if (matchingContacts.Count == 1) SelectedChat = matchingContacts[0];
        }
    }

    // ---------- Публичные ключи собеседников ----------

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

    // ---------- Блокировка (событие Banned от сервера) и смена аккаунта ----------

    private async Task OnBannedAsync(string reason)
    {
        _dialogs.ShowWarning(
            string.IsNullOrEmpty(reason)
                ? "Ваш аккаунт заблокирован администратором."
                : $"Ваш аккаунт заблокирован администратором.\nПричина: {reason}",
            "Блокировка");
        await PerformAccountSwitchAsync();
    }

    [RelayCommand]
    private async Task SwitchAccountAsync() => await PerformAccountSwitchAsync();

    private async Task PerformAccountSwitchAsync()
    {
        await _network.DisconnectAsync(logout: true);
        ConfigService.ClearAutoLoginState();
        var app = (App)Application.Current;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.MainWindow?.Hide();

        try
        {
            var loginWindow = new SecureChat.Client.View.LoginWindow();
            bool? res = loginWindow.ShowDialog();
            if (res == true && loginWindow.AuthenticatedUserId > 0)
            {
                var nextMain = new SecureChat.Client.MainWindow(
                    loginWindow.AuthenticatedUserId, loginWindow.AuthenticatedUserName,
                    loginWindow.Token, loginWindow.ServerUrl,
                    loginWindow.PublicKeyPem, loginWindow.PrivateKeyPem,
                    loginWindow.AuthenticatedUserRole, loginWindow.Network);
                Application.Current.MainWindow = nextMain;
                nextMain.Show();
                app.MainWindow?.Close();
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

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        try
        {
            await _network.ConnectAsync(_serverAddress);
            await _network.HelloAsync(new HelloPayload { ClientId = _clientId, Token = _token });
            await _network.GetContactsAsync();
            UpdateTitleAndStatus();
            _dialogs.ShowInfo("Переподключение выполнено успешно.", "Сервер");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reconnect failed");
            _dialogs.ShowError($"Reconnect failed: {ex.Message}", "Ошибка");
        }
    }

    [RelayCommand]
    private void ExitApplication() => Application.Current.Shutdown();

    // ---------- Администрирование (видно только Admin) ----------

    [RelayCommand]
    private async Task BanSelectedUserAsync()
    {
        if (SelectedChat is not ContactItem contact)
        {
            _dialogs.ShowInfo("Выберите пользователя в списке контактов.", "Администрирование");
            return;
        }

        string? reason = _dialogs.Prompt("Причина блокировки", "");
        if (reason == null) return;

        var resp = await _network.BanUserAsync(new BanUserPayload { TargetId = contact.Id, Reason = reason });
        if (resp?.Status == "Ok")
            _dialogs.ShowInfo($"Пользователь {contact.Name} (ID: {contact.Id}) заблокирован.", "Администрирование");
        else
            _dialogs.ShowError($"Не удалось заблокировать: {MapAdminError(resp?.Status)}", "Ошибка");
    }

    [RelayCommand]
    private async Task UnbanSelectedUserAsync()
    {
        if (SelectedChat is not ContactItem contact)
        {
            _dialogs.ShowInfo("Выберите пользователя в списке контактов.", "Администрирование");
            return;
        }

        var resp = await _network.UnbanUserAsync(new UnbanUserPayload { TargetId = contact.Id });
        if (resp?.Status == "Ok")
            _dialogs.ShowInfo($"Пользователь {contact.Name} (ID: {contact.Id}) разблокирован.", "Администрирование");
        else
            _dialogs.ShowError($"Не удалось разблокировать: {MapAdminError(resp?.Status)}", "Ошибка");
    }

    [RelayCommand]
    private async Task ChangeRoleSelectedUserAsync()
    {
        if (SelectedChat is not ContactItem contact)
        {
            _dialogs.ShowInfo("Выберите пользователя в списке контактов.", "Администрирование");
            return;
        }

        string? role = _dialogs.Prompt("Роль: Admin / Менеджер / Employee", "Employee");
        if (string.IsNullOrWhiteSpace(role)) return;
        // «Менеджер» — русскоязычный ввод роли Manager
        string normalized = role switch
        {
            "Менеджер" => "Manager",
            _ => role
        };
        if (normalized is not ("Admin" or "Manager" or "Employee"))
        {
            _dialogs.ShowError("Недопустимая роль. Доступны: Admin, Менеджер, Employee.", "Ошибка");
            return;
        }

        var resp = await _network.ChangeRoleAsync(new ChangeRolePayload { TargetId = contact.Id, Role = normalized });
        if (resp?.Status == "Ok")
            _dialogs.ShowInfo($"Роль {contact.Name} (ID: {contact.Id}) изменена на {normalized}.", "Администрирование");
        else
            _dialogs.ShowError($"Не удалось сменить роль: {MapAdminError(resp?.Status)}", "Ошибка");
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var settings = new SecureChat.Client.SettingsWindow { Owner = Application.Current.MainWindow };
        settings.ShowDialog();
    }

    [RelayCommand]
    private void OpenAuditLog()
    {
        var window = new SecureChat.Client.View.AuditLogWindow(_network) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    // ---------- Смена пароля ----------

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        var dlg = new SecureChat.Client.View.PasswordDialog { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var resp = await _network.ChangePasswordAsync(new ChangePasswordPayload
        {
            CurrentPassword = dlg.CurrentPassword,
            NewPassword = dlg.NewPassword
        });

        if (resp?.Status == "Ok")
        {
            _dialogs.ShowInfo("Пароль изменён. Все сессии отозваны — выполните вход заново.", "Смена пароля");
            await PerformAccountSwitchAsync();
        }
        else
        {
            _dialogs.ShowError($"Не удалось сменить пароль: {MapAdminError(resp?.Status)}", "Ошибка");
        }
    }

    // ---------- Профиль: статус, аватар, просмотр ----------

    [RelayCommand]
    private async Task ChangeStatusAsync()
    {
        string? status = _dialogs.Prompt("Статус (до 100 символов)", "");
        if (status == null) return;

        var resp = await _network.UpdateStatusAsync(new UpdateStatusPayload { Status = status });
        if (resp?.Status != "Ok")
            _dialogs.ShowError("Статус отклонён: максимум 100 символов, без управляющих символов.", "Ошибка");
    }

    [RelayCommand]
    private async Task UploadAvatarAsync()
    {
        string? path = _dialogs.PickFile("Выберите изображение аватара",
            "Изображения (PNG, JPEG)|*.png;*.jpg;*.jpeg|Все файлы|*.*");
        if (path == null) return;

        try
        {
            byte[] png = AvatarEncoder.EncodeToPng(path);
            var resp = await _network.UploadAvatarAsync(new UploadAvatarPayload
            {
                DataBase64 = Convert.ToBase64String(png)
            });
            if (resp?.Status == "Ok")
                _dialogs.ShowInfo("Аватар обновлён.", "Профиль");
            else
                _dialogs.ShowError($"Аватар отклонён: {MapAdminError(resp?.Status)}", "Ошибка");
        }
        catch (InvalidOperationException ex)
        {
            _dialogs.ShowError(ex.Message, "Ошибка");
        }
    }

    [RelayCommand]
    private async Task ShowMyProfileAsync() => await ShowProfileAsync(_clientId);

    [RelayCommand]
    private async Task ShowSelectedProfileAsync()
    {
        if (SelectedChat is not ContactItem contact)
        {
            _dialogs.ShowInfo("Выберите пользователя в списке контактов.", "Профиль");
            return;
        }
        await ShowProfileAsync(contact.Id);
    }

    private async Task ShowProfileAsync(int targetId)
    {
        var profile = await _network.GetUserProfileAsync(new GetUserProfilePayload { TargetId = targetId });
        if (profile == null)
        {
            _dialogs.ShowInfo("Профиль недоступен: пользователь не найден среди ваших контактов.", "Профиль");
            return;
        }
        var window = new SecureChat.Client.View.ProfileWindow(profile) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    // ---------- Каналы: список, создание, участники, ротация ключа  ----------

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

    [RelayCommand]
    private async Task RefreshChannelsListAsync() => await RefreshChannelsAsync();

    [RelayCommand]
    private async Task CreateChannelAsync()
    {
        var dialog = new SecureChat.Client.View.ChannelCreationWindow(_contacts) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        string name = dialog.ChannelName;
        var selectedMembers = dialog.SelectedMemberIds;

        var resp = await _network.CreateChannelAsync(new CreateChannelPayload { Name = name });
        if (resp?.Status == "Ok")
        {
            // Owner генерирует GroupKey локально
            _channelCrypto.SetGroupKey(resp.ChannelId, _crypto.GenerateSessionKey());

            // Добавляем выбранных участников
            foreach (int memberId in selectedMembers)
            {
                var addResp = await _network.AddChannelMemberAsync(new AddChannelMemberPayload
                {
                    ChannelId = resp.ChannelId,
                    UserId = memberId
                });
                if (addResp?.Status != "Ok")
                {
                    Log.Warning("Failed to add member {UserId} to new channel {ChannelId}: {Status}",
                        memberId, resp.ChannelId, addResp?.Status);
                }
            }

            if (selectedMembers.Count > 0)
            {
                await RotateChannelKeyAsync(resp.ChannelId);
            }

            await RefreshChannelsAsync();
            var created = _channels.FirstOrDefault(c => c.Id == resp.ChannelId);
            if (created != null) SelectedChat = created;
        }
        else
        {
            _dialogs.ShowError($"Не удалось создать канал: {MapChannelError(resp?.Status)}", "Ошибка");
        }
    }

    [RelayCommand]
    private async Task AddChannelMemberAsync()
    {
        if (SelectedChat is not ChannelItem channel)
        {
            _dialogs.ShowInfo(LocalizationSource.Get("SelectChannel"), LocalizationSource.Get("MenuChannels"));
            return;
        }
        if (!int.TryParse(_dialogs.Prompt("ID пользователя для добавления", ""), out int userId) || userId <= 0)
            return;

        var resp = await _network.AddChannelMemberAsync(new AddChannelMemberPayload
        { ChannelId = channel.Id, UserId = userId });
        if (resp?.Status != "Ok")
        {
            _dialogs.ShowError($"Не удалось добавить участника: {MapChannelError(resp?.Status)}", "Ошибка");
            return;
        }

        // backward secrecy: новый ключ доставляется всем, включая нового участника
        await RotateChannelKeyAsync(channel.Id);
        await RefreshChannelsAsync();
    }

    private async Task OnChannelMemberRemovedAsync(ChannelMemberRemovedPayload p)
    {
        var channel = _channels.FirstOrDefault(c => c.Id == p.ChannelId);
        await RefreshChannelsAsync();

        // forward secrecy при бане/удалении: Owner канала ротирует ключ.
        // При ручном удалении ротацию уже выполнил действующий HR/Admin — пропускаем.
        if (channel != null && channel.OwnerId == _clientId &&
            _rotationDoneForRemovedEvent != p.ChannelId)
        {
            await RotateChannelKeyAsync(p.ChannelId);
        }
        if (_rotationDoneForRemovedEvent == p.ChannelId)
            _rotationDoneForRemovedEvent = null;
    }

    [RelayCommand]
    private async Task RemoveChannelMemberAsync()
    {
        if (SelectedChat is not ChannelItem channel)
        {
            _dialogs.ShowInfo(LocalizationSource.Get("SelectChannel"), LocalizationSource.Get("MenuChannels"));
            return;
        }
        if (!int.TryParse(_dialogs.Prompt("ID пользователя для удаления", ""), out int userId) || userId <= 0)
            return;

        var resp = await _network.RemoveChannelMemberAsync(new RemoveChannelMemberPayload
        { ChannelId = channel.Id, UserId = userId });
        if (resp?.Status != "Ok")
        {
            _dialogs.ShowError($"Не удалось удалить участника: {MapChannelError(resp?.Status)}", "Ошибка");
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
        RunOnUi(() =>
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
                    FileSizeText = FormatFileSize(m.FileSize),
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
        if (SelectedChat is ChannelItem ch && ch.Id == channelId)
            CurrentMessages = _allChannelMessages[channelId];
    }

    private void OnIncomingChannelMessage(IncomingChannelMessagePayload msg)
    {
        RunOnUi(() =>
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

                // упоминание @ник: подсветка в имени отправителя
                if (dm.Text.Contains("@" + _userName, StringComparison.OrdinalIgnoreCase))
                    dm.SenderName = "🔔 " + (dm.SenderName == "" ? _userName : dm.SenderName);

                AddChannelMessage(msg.ChannelId, dm);

                // счётчик непрочитанных канала
                if (SelectedChat is not ChannelItem current || current.Id != msg.ChannelId)
                {
                    var channelItem = _channels.FirstOrDefault(c => c.Id == msg.ChannelId);
                    if (channelItem != null) channelItem.UnreadCount++;
                }
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
            _dialogs.ShowInfo("Ключ канала ещё не получен — дождитесь доставки от владельца.", "Каналы");
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

        MessageText = "";
        ClearReply();
    }

    /// <summary>Отправка файла в канал: ≤10 МБ, шифрование MessageKey из ratchet.</summary>
    private async Task SendChannelFileAsync()
    {
        if (SelectedChat is not ChannelItem channel)
        {
            _dialogs.ShowInfo(LocalizationSource.Get("SelectChannel"), LocalizationSource.Get("Info"));
            return;
        }

        var next = _channelCrypto.GetNextMessageKey(channel.Id, _clientId);
        if (next == null)
        {
            _dialogs.ShowInfo("Ключ канала ещё не получен — дождитесь доставки от владельца.", "Каналы");
            return;
        }

        string? path = _dialogs.PickFile("Выберите файл", "Все файлы|*.*");
        if (path == null) return;

        var file = new FileInfo(path);
        if (file.Length > 10 * 1024 * 1024)
        {
            _dialogs.ShowError("Файл больше 10 МБ.", "Ошибка");
            return;
        }

        try
        {
            var (messageKey, iteration) = next.Value;
            byte[] fileData = await File.ReadAllBytesAsync(path);
            var (ct, nonce, tag) = _crypto.AesGcmEncrypt(messageKey, fileData);
            string msgId = Guid.NewGuid().ToString();
            string fileName = Path.GetFileName(path);

            AddChannelMessage(channel.Id, new DialogMessage
            {
                Id = msgId,
                SenderId = _clientId,
                FilePath = fileName,
                FileSizeText = FormatFileSize(fileData.Length),
                ContentType = "File",
                Status = "",
                IsOutgoing = true,
                Timestamp = DateTime.Now
            });

            await _network.SendChannelMessageAsync(new SendChannelMessagePayload
            {
                ChannelId = channel.Id,
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
                channel.Id, fileName, fileData.Length, iteration, msgId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send channel file channelId={ChannelId}", channel.Id);
            _dialogs.ShowError($"Не удалось отправить файл: {ex.Message}", "Ошибка");
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
                FileSizeText = FormatFileSize(plain.Length),
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

    // ---------- Reply ----------

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
        if (SelectedChat is ChannelItem channel)
            return GetChannelMemberName(channel.Id, msg.SenderId);
        var contact = _contacts.FirstOrDefault(c => c.Id == msg.SenderId);
        return contact != null ? contact.RawName : $"User {msg.SenderId}";
    }

    private void StartReply(DialogMessage msg)
    {
        _replyToMessageId = msg.Id;
        string author = msg.IsOutgoing ? _userName : ResolveSenderName(msg);
        string preview = msg.ContentType == "Text" ? msg.Text : (msg.FilePath ?? "файл");
        if (preview.Length > 100) preview = preview[..100] + "…";
        ReplyBarText = $"Ответ на: {author}: {preview}";
        IsReplyBarVisible = true;
        FocusMessageRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ReplyToMessage()
    {
        if (SelectedMessage is DialogMessage msg)
            StartReply(msg);
    }

    [RelayCommand]
    private void CancelReply()
    {
        if (_editingMessageId != null)
        {
            _editingMessageId = null;
            MessageText = "";
        }
        ClearReply();
    }

    private void ClearReply()
    {
        _replyToMessageId = null;
        IsReplyBarVisible = false;
    }

    /// <summary>Редактирование: новый шифртекст тем же сессионным ключом (сервер проверяет окно 15 минут).</summary>
    private async Task EditCurrentMessageAsync(string newText)
    {
        if (_currentContactId <= 0 || _editingMessageId == null) return;

        byte[]? sessionKey = _dialogManager.GetSessionKey(_currentContactId);
        if (sessionKey == null)
        {
            _dialogs.ShowError("Сессионный ключ диалога недоступен.", "Ошибка");
            _editingMessageId = null;
            ClearReply();
            return;
        }

        var (ct, nonce, tag) = _crypto.AesGcmEncrypt(sessionKey, Encoding.UTF8.GetBytes(newText));
        var resp = await _network.EditMessageAsync(new EditMessagePayload
        {
            MessageId = _editingMessageId,
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(ct),
            Tag = Convert.ToBase64String(tag)
        });

        if (resp?.Status == "Ok")
        {
            if (_allDialogs.TryGetValue(_currentContactId, out var dialog))
            {
                var msg = dialog.FirstOrDefault(m => m.Id == _editingMessageId);
                if (msg != null)
                {
                    msg.Text = newText;
                    msg.IsEdited = true;
                }
            }
            RefreshCurrentView();
            Log.Information("Message edited messageId={MessageId}", _editingMessageId);
        }
        else
        {
            _dialogs.ShowError($"Не удалось изменить сообщение: {MapAdminError(resp?.Status)}", "Ошибка");
        }

        _editingMessageId = null;
        ClearReply();
    }

    // ---------- Edit / Delete ----------

    [RelayCommand]
    private void EditMessage()
    {
        if (SelectedMessage is DialogMessage msg)
        {
            // редактирование — только свои текстовые сообщения Direct-чатов
            if (!msg.IsOutgoing || msg.ContentType != "Text" || SelectedChat is ChannelItem)
            {
                _dialogs.ShowInfo("Изменять можно только свои текстовые сообщения в личных чатах.",
                    LocalizationSource.Get("Info"));
                return;
            }
            if (DateTime.Now - msg.Timestamp > TimeSpan.FromMinutes(15))
            {
                _dialogs.ShowInfo("Срок редактирования истёк (15 минут).",
                    LocalizationSource.Get("Info"));
                return;
            }

            _editingMessageId = msg.Id;
            _replyToMessageId = null;
            ReplyBarText = "Изменение сообщения";
            IsReplyBarVisible = true;
            MessageText = msg.Text;
            FocusMessageRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private async Task DeleteMessageAsync()
    {
        if (SelectedMessage is DialogMessage msg)
        {
            if (msg.IsOutgoing || _role == "Manager" || _role == "Admin")
            {
                var resp = await _network.DeleteMessageAsync(new DeleteMessagePayload { MessageId = msg.Id });
                if (resp?.Status == "Ok") MarkMessageDeletedLocally(msg.Id);
                else _dialogs.ShowError($"Не удалось удалить: {MapAdminError(resp?.Status)}", "Ошибка");
            }
            else
            {
                _dialogs.ShowInfo("Удалять можно свои сообщения.",
                    LocalizationSource.Get("Info"));
            }
        }
    }

    /// <summary>Локальная пометка об удалении (у второй стороны — по событию).</summary>
    private void MarkMessageDeletedLocally(string messageId)
    {
        foreach (var dialog in _allDialogs.Values)
        {
            var msg = dialog.FirstOrDefault(m => m.Id == messageId);
            if (msg != null)
            {
                msg.Text = "Сообщение удалено";
                msg.ContentType = "Text";
            }
        }
        foreach (var dialog in _allChannelMessages.Values)
        {
            var msg = dialog.FirstOrDefault(m => m.Id == messageId);
            if (msg != null)
            {
                msg.Text = "Сообщение удалено";
                msg.ContentType = "Text";
            }
        }
        RefreshCurrentView();
    }

    private void OnMessageDeleted(MessageDeletedPayload payload)
    {
        RunOnUi(() => MarkMessageDeletedLocally(payload.MessageId));
    }

    private void OnMessageEdited(MessageEditedPayload payload)
    {
        RunOnUi(() =>
        {
            foreach (var pair in _allDialogs)
            {
                var msg = pair.Value.FirstOrDefault(m => m.Id == payload.MessageId);
                if (msg == null) continue;

                byte[]? sessionKey = _dialogManager.GetSessionKey(pair.Key);
                if (sessionKey == null) return;
                try
                {
                    byte[] plain = _crypto.AesGcmDecrypt(sessionKey,
                        Convert.FromBase64String(payload.Nonce),
                        Convert.FromBase64String(payload.Ciphertext),
                        Convert.FromBase64String(payload.Tag));
                    msg.Text = Encoding.UTF8.GetString(plain);
                    msg.IsEdited = true;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Edited message decrypt failed messageId={MessageId}", payload.MessageId);
                }
            }
            RefreshCurrentView();
        });
    }

    /// <summary>Обновление видимых свойств сообщений (модель без INPC).</summary>
    private void RefreshCurrentView()
    {
        if (CurrentMessages != null)
            CollectionViewSource.GetDefaultView(CurrentMessages).Refresh();
    }

    // ---------- Поиск по выбранному чату (строка поиска) ----------

    /// <summary>Ctrl+F: показать/скрыть строку поиска в текущем чате.</summary>
    [RelayCommand]
    private void ToggleChatSearch()
    {
        if (IsChatSearchVisible)
        {
            CloseChatSearch();
            return;
        }
        IsChatSearchVisible = true;
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CloseChatSearch()
    {
        IsChatSearchVisible = false;
        ChatSearchQuery = "";
        _chatSearchMatches.Clear();
        _chatSearchIndex = -1;
        SelectedMessage = null;
    }

    partial void OnChatSearchQueryChanged(string value) => RunChatSearch();

    private ObservableCollection<DialogMessage>? CurrentChatCollection()
    {
        if (SelectedChat is ChannelItem channel)
            return _allChannelMessages.TryGetValue(channel.Id, out var c) ? c : null;
        if (_currentContactId > 0)
            return _allDialogs.TryGetValue(_currentContactId, out var d) ? d : null;
        return null;
    }

    private void RunChatSearch()
    {
        _chatSearchMatches.Clear();
        _chatSearchIndex = -1;
        SelectedMessage = null;

        string query = ChatSearchQuery.Trim();
        if (query.Length < 1)
        {
            ChatSearchCount = "";
            return;
        }

        var collection = CurrentChatCollection();
        if (collection == null)
        {
            ChatSearchCount = "Выберите чат";
            return;
        }

        _chatSearchMatches = collection
            .Where(m => m.ContentType == "Text" && m.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        ChatSearchCount = _chatSearchMatches.Count == 0 ? "Нет совпадений" : $"1/{_chatSearchMatches.Count}";
        if (_chatSearchMatches.Count > 0) ShowChatSearchMatch(0);
    }

    [RelayCommand]
    private void NextChatSearchMatch()
    {
        if (_chatSearchMatches.Count == 0) return;
        ShowChatSearchMatch((_chatSearchIndex + 1) % _chatSearchMatches.Count);
    }

    private void ShowChatSearchMatch(int index)
    {
        _chatSearchIndex = index;
        var msg = _chatSearchMatches[index];
        SelectedMessage = msg;
        ScrollToMessageRequested?.Invoke(this, msg);
        ChatSearchCount = $"{index + 1}/{_chatSearchMatches.Count}";
    }

    // ---------- Экспорт текущего чата (только клиент) ----------

    /// <summary>Строки текущего чата для экспорта: расшифрованные текстовые сообщения.</summary>
    public List<ChatExportRow>? CollectCurrentChatRows()
    {
        if (SelectedChat is ChannelItem channel)
        {
            if (!_allChannelMessages.TryGetValue(channel.Id, out var channelCol)) return null;
            return channelCol
                .Where(m => m.ContentType == "Text" && m.Text != "Сообщение удалено")
                .Select(m => new ChatExportRow
                {
                    Id = m.Id,
                    Sender = m.IsOutgoing ? _userName : GetChannelMemberName(channel.Id, m.SenderId),
                    Text = m.Text,
                    TimestampUtc = m.Timestamp.ToUniversalTime()
                })
                .ToList();
        }

        if (_currentContactId <= 0 ||
            !_allDialogs.TryGetValue(_currentContactId, out var dialog)) return null;

        var contact = _contacts.FirstOrDefault(c => c.Id == _currentContactId);
        string contactName = contact?.RawName ?? $"User {_currentContactId}";
        return dialog
            .Where(m => m.ContentType == "Text" && m.Text != "Сообщение удалено")
            .Select(m => new ChatExportRow
            {
                Id = m.Id,
                Sender = m.IsOutgoing ? _userName : contactName,
                Text = m.Text,
                TimestampUtc = m.Timestamp.ToUniversalTime()
            })
            .ToList();
    }

    /// <summary>Строки-вложения текущего чата (имя файла, без содержимого).</summary>
    public List<ChatExportRow>? CollectCurrentChatFileRows()
    {
        if (SelectedChat is ChannelItem channel)
        {
            if (!_allChannelMessages.TryGetValue(channel.Id, out var channelCol)) return null;
            return channelCol
                .Where(m => m.ContentType == "File")
                .Select(m => new ChatExportRow
                {
                    Id = m.Id,
                    Sender = m.IsOutgoing ? _userName : GetChannelMemberName(channel.Id, m.SenderId),
                    Text = "📎 " + Path.GetFileName(m.FilePath ?? "файл"),
                    TimestampUtc = m.Timestamp.ToUniversalTime()
                })
                .ToList();
        }

        if (_currentContactId <= 0 || !_allDialogs.TryGetValue(_currentContactId, out var dialog)) return null;
        var contact = _contacts.FirstOrDefault(c => c.Id == _currentContactId);
        string contactName = contact?.RawName ?? $"User {_currentContactId}";
        return dialog
            .Where(m => m.ContentType == "File")
            .Select(m => new ChatExportRow
            {
                Id = m.Id,
                Sender = m.IsOutgoing ? _userName : contactName,
                Text = "📎 " + Path.GetFileName(m.FilePath ?? "файл"),
                TimestampUtc = m.Timestamp.ToUniversalTime()
            })
            .ToList();
    }

    /// <summary>Заголовок текущего чата (для печати в представлении).</summary>
    public string GetChatTitle()
    {
        if (SelectedChat is ChannelItem channel)
            return "# " + channel.Name;
        var contact = _contacts.FirstOrDefault(c => c.Id == _currentContactId);
        return contact?.RawName ?? $"User {_currentContactId}";
    }

    [RelayCommand]
    private void ExportExcel()
    {
        var rows = CollectCurrentChatRows();
        if (rows == null)
        {
            _dialogs.ShowInfo("Выберите чат для экспорта.", "Экспорт");
            return;
        }

        string? fileName = _dialogs.PickSaveFile("Экспорт в Excel", "Книга Excel (*.xlsx)|*.xlsx",
            PathSanitizer.SanitizeFileName($"chat-{DateTime.Now:yyyyMMdd-HHmm}", "chat") + ".xlsx");
        if (fileName == null) return;

        try
        {
            var stats = ChatAggregator.Aggregate(rows);
            new ExcelStatsRenderer().Render(stats, fileName);
            Log.Information("Chat exported to Excel {File} rows={Count}", fileName, stats.TotalMessages);
            _dialogs.ShowInfo($"Экспорт выполнен: {stats.TotalMessages:N0} сообщений.\n{fileName}", "Экспорт");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Excel export failed");
            _dialogs.ShowError($"Ошибка экспорта: {ex.Message}", "Ошибка");
        }
    }

    [RelayCommand]
    private void ExportWord()
    {
        var rows = CollectCurrentChatRows();
        if (rows == null)
        {
            _dialogs.ShowInfo("Выберите чат для экспорта.", "Экспорт");
            return;
        }

        string? fileName = _dialogs.PickSaveFile("Экспорт в Word", "Документ Word (*.docx)|*.docx",
            PathSanitizer.SanitizeFileName($"chat-{DateTime.Now:yyyyMMdd-HHmm}", "chat") + ".docx");
        if (fileName == null) return;

        try
        {
            var stats = ChatAggregator.Aggregate(rows);
            new WordStatsRenderer().Render(stats, fileName);
            Log.Information("Chat exported to Word {File} rows={Count}", fileName, stats.TotalMessages);
            _dialogs.ShowInfo($"Экспорт выполнен: {stats.TotalMessages:N0} сообщений.\n{fileName}", "Экспорт");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Word export failed");
            _dialogs.ShowError($"Ошибка экспорта: {ex.Message}", "Ошибка");
        }
    }

    // ---------- Справка и прочее ----------

    [RelayCommand]
    private void ShowMyPublicKey()
    {
        Clipboard.SetText(_publicKeyPem);
        _dialogs.ShowInfo("Публичный ключ скопирован в буфер обмена.", "Ключ");
    }

    [RelayCommand]
    private void ShowAbout()
    {
        _dialogs.ShowInfo("SecureChat Enterprise v2.0\n\nКорпоративный мессенджер на WebSocket со сквозным шифрованием (RSA-2048 + AES-256-GCM).",
            "О программе");
    }

    [RelayCommand]
    private void ShowHelpGuide()
    {
        var help = new SecureChat.Client.HelpWindow();
        help.Owner = Application.Current.MainWindow;
        help.Show();
    }

    // ---------- События профиля контактов ----------

    private void OnUserProfileUpdated(UserProfileUpdatedPayload update)
    {
        RunOnUi(() =>
        {
            var contact = _contacts.FirstOrDefault(c => c.Id == update.UserId);
            if (contact != null) contact.RawName = update.UserName;
        });
    }

    private void OnUserInfoResponse(UserInfoResponsePayload info)
    {
        RunOnUi(() =>
        {
            if (info.Status == "Ok")
            {
                var contact = _contacts.FirstOrDefault(c => c.Id == info.UserId);
                if (contact != null) contact.RawName = info.UserName;
            }
        });
    }

    // ---------- Direct-история (курсорная пагинация) ----------

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

        RunOnUi(() =>
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
                        Status = MapStatusGlyph(hm.Status),
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
                        FileSizeText = FormatFileSize(hm.FileSize),
                        ContentType = "File",
                        Status = MapStatusGlyph(hm.Status),
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
                CurrentMessages = dialog;
                UpdateLoadMoreVisibility();
            }
        });
    }

    private void UpdateLoadMoreVisibility()
    {
        bool hasMore = SelectedChat is ChannelItem channel
            ? _channelHasMore.TryGetValue(channel.Id, out var ch) && ch
            : _currentContactId > 0 && _directHasMore.TryGetValue(_currentContactId, out var dh) && dh;
        IsLoadMoreVisible = hasMore;
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (SelectedChat is ChannelItem channel)
        {
            await LoadChannelHistoryPageAsync(channel.Id, loadMore: true);
        }
        else if (_currentContactId > 0)
        {
            await LoadDirectHistoryAsync(_currentContactId, loadMore: true);
        }
        UpdateLoadMoreVisibility();
    }

    // ---------- Список контактов (событие от сервера) ----------

    private void OnContactList(ContactListPayload list)
    {
        RunOnUi(() =>
        {
            _contacts.Clear();
            foreach (var user in list.Contacts)
            {
                if (!_allDialogs.ContainsKey(user.Id))
                    _allDialogs[user.Id] = new ObservableCollection<DialogMessage>();
                _contacts.Add(new ContactItem { Id = user.Id, RawName = user.Name, IsOnline = user.IsOnline });
            }
            if (_contacts.Count > 0 && _currentContactId == -1 && SelectedChat == null)
                SelectedChat = _contacts[0];
            Log.Debug("ContactList received count={Count}", list.Contacts.Count);
        });
    }

    // ---------- Выбор чата (вместо ContactsListBox_SelectionChanged) ----------

    partial void OnSelectedChatChanged(object? value) => _ = HandleSelectedChatChangedAsync(value);

    private async Task HandleSelectedChatChangedAsync(object? value)
    {
        try
        {
            // единый список: выбор канала
            if (value is ChannelItem channel)
            {
                _currentContactId = -1;
                channel.UnreadCount = 0;

                if (!_allChannelMessages.ContainsKey(channel.Id))
                    _allChannelMessages[channel.Id] = new ObservableCollection<DialogMessage>();
                CurrentMessages = _allChannelMessages[channel.Id];

                await CacheChannelMemberNamesAsync(channel.Id);
                await EnsureChannelHistoryAsync(channel.Id);
                UpdateLoadMoreVisibility();
                return;
            }

            // выбор контакта
            if (value is ContactItem contact)
            {
                _currentContactId = contact.Id;
                contact.UnreadCount = 0;
                if (!_allDialogs.ContainsKey(_currentContactId))
                    _allDialogs[_currentContactId] = new ObservableCollection<DialogMessage>();
                CurrentMessages = _allDialogs[_currentContactId];

                if (_allDialogs[_currentContactId].Count == 0)
                    await LoadDirectHistoryAsync(contact.Id, loadMore: false);

                // Read-статусы: диалог открыт — все входящие сообщения прочитаны
                foreach (var m in _allDialogs[_currentContactId].Where(m => !m.IsOutgoing))
                    await _network.AckAsync(new AckPayload { MessageId = m.Id, Status = "Read" });

                UpdateLoadMoreVisibility();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to handle selected chat");
        }
    }

    // ---------- Статусы доставки и входящие сообщения ----------

    private void OnDeliveryStatus(DeliveryStatusPayload status)
    {
        RunOnUi(() =>
        {
            Log.Debug("DeliveryStatus messageId={MessageId} status={Status}", status.MessageId, status.Status);
            var msg = _allDialogs.Values.SelectMany(c => c).FirstOrDefault(m => m.Id == status.MessageId);
            if (msg != null)
            {
                msg.Status = MapStatusGlyph(status.Status);
                RefreshCurrentView();
            }
        });
    }

    private void OnIncomingMessage(IncomingMessagePayload msg)
    {
        RunOnUi(() =>
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

                // счётчик непрочитанных: сообщение пришло не в открытый диалог
                if (SelectedChat is ChannelItem || _currentContactId != msg.SenderId)
                {
                    var contact = _contacts.FirstOrDefault(c => c.Id == msg.SenderId);
                    if (contact != null) contact.UnreadCount++;
                }

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
                            RunOnUi(() =>
                            {
                                AddMessage(senderId, new DialogMessage
                                {
                                    Id = messageId,
                                    SenderId = senderId,
                                    ReceiverId = _clientId,
                                    FilePath = path,
                                    FileSizeText = FormatFileSize(new FileInfo(path).Length),
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
                        RunOnUi(() => _dialogs.ShowError($"Ошибка приёма файла: {ex.Message}", "Ошибка"));
                    }
                });
            }
        });
    }

    private void OnAddContactResponse(AddContactResponsePayload response)
    {
        RunOnUi(() =>
        {
            if (response.Status == "NotFound")
            {
                _dialogs.ShowError($"Сотрудник с ID {response.ContactId} не найден на сервере.", "Ошибка");
                var contact = _contacts.FirstOrDefault(c => c.Id == response.ContactId);
                if (contact != null)
                {
                    _contacts.Remove(contact);
                    if (_allDialogs.ContainsKey(response.ContactId)) _allDialogs.Remove(response.ContactId);
                    if (_currentContactId == response.ContactId)
                    {
                        _currentContactId = -1;
                        CurrentMessages = null;
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
            CurrentMessages = _allDialogs[contactId];
    }

    // ---------- Отправка ----------

    [RelayCommand]
    private async Task SendTextAsync()
    {
        // режим редактирования: перешифровка текста тем же сессионным ключом
        if (_editingMessageId != null && SelectedChat is not ChannelItem)
        {
            string editText = MessageText.Trim();
            if (string.IsNullOrEmpty(editText)) return;
            await EditCurrentMessageAsync(editText);
            MessageText = "";
            return;
        }

        // режим «Каналы»: сообщение уходит в канал через групповой ключ
        if (SelectedChat is ChannelItem channel)
        {
            string channelText = MessageText.Trim();
            if (string.IsNullOrEmpty(channelText)) return;
            if (channelText.Length > 4000)
            {
                _dialogs.ShowInfo(LocalizationSource.Get("MessageTooLong"),
                    LocalizationSource.Get("Info"));
                return;
            }
            await SendChannelMessageTextAsync(channel.Id, channelText);
            MessageText = "";
            return;
        }

        if (_currentContactId <= 0)
        {
            _dialogs.ShowInfo(LocalizationSource.Get("SelectRecipient"), LocalizationSource.Get("Info"));
            return;
        }
        // NFC-нормализация до шифрования
        string text = TextNormalizer.NormalizeToNfc(MessageText.Trim());
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > 4000)
        {
            _dialogs.ShowInfo(LocalizationSource.Get("MessageTooLong"),
                LocalizationSource.Get("Info"));
            return;
        }

        try
        {
            string? pubKey = await EnsurePublicKeyAsync(_currentContactId);
            if (pubKey == null)
            {
                _dialogs.ShowError("Не удалось получить открытый ключ собеседника.", "Ошибка");
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

            MessageText = "";
            ClearReply();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send message contactId={ContactId}", _currentContactId);
            _dialogs.ShowError($"Не удалось отправить сообщение: {ex.Message}", "Ошибка");
        }
    }

    [RelayCommand]
    private async Task AttachFileAsync()
    {
        // режим «Каналы»: файл шифруется MessageKey из ratchet и уходит в канал
        if (SelectedChat is ChannelItem)
        {
            await SendChannelFileAsync();
            return;
        }

        if (_currentContactId <= 0)
        {
            _dialogs.ShowInfo(LocalizationSource.Get("SelectRecipient"), LocalizationSource.Get("Info"));
            return;
        }
        try
        {
            string? pubKey = await EnsurePublicKeyAsync(_currentContactId);
            if (pubKey == null)
            {
                _dialogs.ShowError("Таймаут получения открытого ключа.", "Ошибка");
                return;
            }

            string? path = _dialogs.PickFile("Выберите файл", "Все файлы|*.*");
            if (path == null) return;

            byte[] sessionKey = _dialogManager.GetOrCreateSessionKey(_currentContactId);
            byte[] encKey = _crypto.RsaEncrypt(pubKey, sessionKey);
            byte[] encKeySelf = _crypto.RsaEncrypt(_publicKeyPem, sessionKey);

            string msgId = await _fileTransfer.SendFileAsync(
                _currentContactId, path, sessionKey,
                Convert.ToBase64String(encKey), Convert.ToBase64String(encKeySelf));

            AddMessage(_currentContactId, new DialogMessage
            {
                Id = msgId,
                SenderId = _clientId,
                ReceiverId = _currentContactId,
                FilePath = Path.GetFileName(path),
                FileSizeText = FormatFileSize(new FileInfo(path).Length),
                ContentType = "File",
                Status = "Отправка...",
                IsOutgoing = true,
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send file contactId={ContactId}", _currentContactId);
            _dialogs.ShowError($"Не удалось отправить файл: {ex.Message}", "Ошибка");
        }
    }

    // ---------- Тема и имя пользователя ----------

    [RelayCommand]
    private void ToggleTheme()
    {
        string newTheme = App.CurrentTheme == "Dark" ? "Light" : "Dark";
        App.ApplyTheme(newTheme);
        Log.Information("Theme changed to {Theme}", newTheme);
    }

    [RelayCommand]
    private async Task ChangeNameAsync()
    {
        string? newName = _dialogs.Prompt("Имя пользователя", _userName);
        if (!string.IsNullOrWhiteSpace(newName) && newName != _userName)
        {
            _userName = newName.Trim();
            UpdateTitleAndStatus();
            await _network.UpdateProfileAsync(new UpdateProfilePayload { UserName = _userName });
            Log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} newName={NewName}",
                "ProfileUpdate", _clientId, _userName);
        }
    }
}
