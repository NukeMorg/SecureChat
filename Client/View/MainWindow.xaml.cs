using SecureChat.Client.Models;
using SecureChat.Client.Properties;
using SecureChat.Client.Services;
using SecureChat.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SecureChat.Client;

public partial class MainWindow : Window
{
    private NetworkService _network = new();
    private CryptoService _crypto = new();
    private DialogManager _dialogManager;
    private FileTransferManager _fileTransfer;
    private int _clientId;
    private string _publicKeyXml, _privateKeyXml, _serverAddress;
    private string _userName = "User";

    private int _currentContactId = -1;
    private Dictionary<int, ObservableCollection<DialogMessage>> _allDialogs = new();
    private ObservableCollection<ContactItem> _contacts = new();
    private TaskCompletionSource<int>? _registrationTcs;

    public static readonly RoutedCommand ExitCommand = new();
    public static readonly RoutedCommand ToggleThemeCommand = new();
    public static readonly RoutedCommand ChangeNameCommand = new();
    public static readonly RoutedCommand ChangeServerAddressCommand = new();
    public static readonly RoutedCommand SettingsCommand = new();

    public MainWindow()
    {
        InitializeComponent();
        _dialogManager = new DialogManager(_crypto);
        _fileTransfer = new FileTransferManager(_crypto, _network);

        _network.OnRegisterResponse += OnRegistered;
        _network.OnIncomingMessage += OnIncomingMessage;
        _network.OnDeliveryStatus += OnDeliveryStatus;
        _network.OnConnected += () => Dispatcher.Invoke(() => UpdateConnectionStatus(true));
        _network.OnDisconnected += () => Dispatcher.Invoke(() => UpdateConnectionStatus(false));
        _network.OnError += err => Dispatcher.Invoke(() => MessageBox.Show($"Ошибка: {err}"));
        _network.OnUserProfileUpdated += OnUserProfileUpdated;
        _network.OnUserInfoResponse += OnUserInfoResponse;
        _network.OnHistoryResponse += OnHistoryResponse;
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

        CommandBindings.Add(new CommandBinding(ExitCommand, (s, e) => Application.Current.Shutdown()));
        CommandBindings.Add(new CommandBinding(ToggleThemeCommand, (s, e) => ToggleTheme_Click(s, e)));
        CommandBindings.Add(new CommandBinding(ChangeNameCommand, (s, e) => ChangeName_Click(s, e)));
        CommandBindings.Add(new CommandBinding(ChangeServerAddressCommand, (s, e) => ChangeServerAddress_Click(s, e)));
        CommandBindings.Add(new CommandBinding(SettingsCommand, (s, e) => { /* Открыть окно настроек */ }));

        this.Loaded += MainWindow_Loaded;

        ContactsListBox.ItemsSource = _contacts;
    }

    // ---------- Подключение и конфигурация ----------
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        this.Loaded -= MainWindow_Loaded;
        await LoadConfigAndConnectAsync();
    }

    private void AutoDiscover_Click(object sender, RoutedEventArgs e)
    {
        // Логика автоматического поиска сервера (уже есть в начале загрузки)
        _ = Dispatcher.InvokeAsync(async () =>
        {
            string? addr = await DiscoverServerAsync();
            if (!string.IsNullOrEmpty(addr))
            {
                _serverAddress = addr;
                SaveConfig();
                MessageBox.Show($"Найден сервер: {addr}", "Обнаружение", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Сервер не найден в локальной сети.", "Обнаружение", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                _network.Disconnect();
                var parts = _serverAddress.Split(':');
                await _network.ConnectAsync(parts[0], int.Parse(parts[1]));
                await _network.SendAsync(new { Type = "Hello", Payload = new HelloPayload { ClientId = _clientId } });
                await RequestContactsAsync();
                UpdateTitleAndStatus();
                MessageBox.Show("Переподключение выполнено.", "Сервер", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка переподключения: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    private void SetFontSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string sizeStr && double.TryParse(sizeStr, out double size))
        {
            Properties.Settings.Default.Save();
        }
    }

    private void ShowMyPublicKey_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_publicKeyXml);
        MessageBox.Show("Публичный ключ скопирован в буфер обмена.", "Ключ", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("SecureChat v2.0\n\nБезопасный чат с оконечным шифрованием.\nИспользует RSA+AES-GCM.\n",
                        "О программе", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void HelpGuide_Click(object sender, RoutedEventArgs e)
    {
        var helpWindow = new HelpWindow();
        helpWindow.Owner = this;
        helpWindow.Show();
    }

    private async Task LoadConfigAndConnectAsync()
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\SecureChat";
        Directory.CreateDirectory(dir);
        string configFile = dir + "\\config.json";

        if (File.Exists(configFile))
        {
            var cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(configFile));
            _clientId = int.Parse(cfg["ClientId"]);
            _publicKeyXml = cfg["PublicKey"];
            _privateKeyXml = cfg["PrivateKey"];
            _serverAddress = cfg["ServerAddress"];
            _userName = cfg.ContainsKey("UserName") ? cfg["UserName"] : "User";

            while (true)
            {
                try
                {
                    var parts = _serverAddress.Split(':');
                    await _network.ConnectAsync(parts[0], int.Parse(parts[1]));
                    await _network.SendAsync(new { Type = "Hello", Payload = new HelloPayload { ClientId = _clientId } });
                    UpdateTitleAndStatus();
                    await RequestContactsAsync();
                    break;
                }
                catch (SocketException)
                {
                    string? newAddress = await DiscoverServerAsync();
                    if (!string.IsNullOrEmpty(newAddress))
                    {
                        _serverAddress = newAddress;
                        SaveConfig();
                        continue;
                    }
                    bool manualEntry = false;
                    Dispatcher.Invoke(() =>
                    {
                        var result = MessageBox.Show("Не удалось автоматически найти сервер.\nВвести адрес вручную?",
                            "Сервер не найден", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        manualEntry = (result == MessageBoxResult.Yes);
                    });
                    if (manualEntry)
                    {
                        string? manualAddress = ShowInputDialog("Адрес сервера (ip:port)", _serverAddress);
                        if (!string.IsNullOrWhiteSpace(manualAddress))
                        {
                            _serverAddress = manualAddress.Trim();
                            SaveConfig();
                            continue;
                        }
                    }
                    Dispatcher.Invoke(() => MessageBox.Show("Приложение будет закрыто.", "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error));
                    Application.Current.Shutdown();
                    return;
                }
            }
        }
        else
        {
            // Первый запуск – сразу запрашиваем адрес сервера без автообнаружения
            string? manualAddress = ShowInputDialog("Адрес сервера (ip:port)", "192.168.1.100:9000");
            if (string.IsNullOrWhiteSpace(manualAddress))
            {
                MessageBox.Show("Адрес не указан. Приложение будет закрыто.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            string discoveredAddress = manualAddress.Trim();

            _serverAddress = discoveredAddress;
            var keys = _crypto.GenerateRsaKeyPair();
            _publicKeyXml = keys.publicKey;
            _privateKeyXml = keys.privateKey;

            int attempt = 0;
            const int maxAttempts = 5;
            while (attempt < maxAttempts)
            {
                try
                {
                    var parts = _serverAddress.Split(':');
                    await _network.ConnectAsync(parts[0], int.Parse(parts[1]));

                    _registrationTcs = new TaskCompletionSource<int>();
                    await _network.SendAsync(new { Type = "Register", Payload = new RegisterPayload { PublicKey = _publicKeyXml } });
                    _clientId = await _registrationTcs.Task;
                    _userName = "User";
                    SaveConfig();
                    await _network.SendAsync(new { Type = "Hello", Payload = new HelloPayload { ClientId = _clientId } });
                    UpdateTitleAndStatus();
                    await RequestContactsAsync();
                    break;
                }
                catch (SocketException)
                {
                    attempt++;
                    if (attempt >= maxAttempts)
                    {
                        Dispatcher.Invoke(() => MessageBox.Show(
                            $"Не удалось подключиться к серверу после {maxAttempts} попыток.",
                            "Ошибка",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error));
                        Application.Current.Shutdown();
                        return;
                    }
                    await Task.Delay(2000);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => MessageBox.Show(
                        $"Критическая ошибка: {ex.Message}",
                        "Ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error));
                    Application.Current.Shutdown();
                    return;
                }
            }
        }
    }

    private async Task RequestContactsAsync()
    {
        try { await _network.SendAsync(new { Type = "GetContacts" }); }
        catch { }
    }

    private async Task<string?> DiscoverServerAsync(int timeoutMs = 2000)
    {
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        byte[] data = Encoding.UTF8.GetBytes("DISCOVER_SECURECHAT_SERVER");
        await udp.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Broadcast, 9001));
        var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var receiveTask = udp.ReceiveAsync();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(timeoutMs, cts.Token));
            if (completed == receiveTask)
            {
                var result = await receiveTask;
                if (Encoding.UTF8.GetString(result.Buffer) == "SECURECHAT_SERVER_RESPONSE")
                    return $"{result.RemoteEndPoint.Address}:9000";
            }
        }
        catch { }
        return null;
    }

    private void OnRegistered(int id) => _registrationTcs?.TrySetResult(id);

    private void SaveConfig()
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\SecureChat";
        Directory.CreateDirectory(dir);
        var cfg = new Dictionary<string, string>
        {
            ["ClientId"] = _clientId.ToString(),
            ["PublicKey"] = _publicKeyXml,
            ["PrivateKey"] = _privateKeyXml,
            ["ServerAddress"] = _serverAddress,
            ["UserName"] = _userName
        };
        File.WriteAllText(dir + "\\config.json", JsonSerializer.Serialize(cfg));
    }

    private void UpdateTitleAndStatus()
    {
        Title = $"SecureChat - {_userName} (ID: {_clientId})";
        UserIdTextBlock.Text = $"{_userName} (ID: {_clientId})";
    }

    private void UpdateConnectionStatus(bool connected)
    {
        ConnectionIndicator.Fill = connected ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Gray);
        UserIdTextBlock.Text = connected ? $"{_userName} (ID: {_clientId})" : $"{_userName} (ID: {_clientId}) (отключено)";
    }

    // ---------- Обработчики сетевых событий ----------
    private void OnUserProfileUpdated(UserProfileUpdatedPayload update)
    {
        Dispatcher.Invoke(() =>
        {
            var contact = _contacts.FirstOrDefault(c => c.Id == update.UserId);
            if (contact != null) contact.Name = update.UserName;
        });
    }

    private void OnUserInfoResponse(UserInfoResponsePayload info)
    {
        Dispatcher.Invoke(() =>
        {
            if (info.Status == "Ok")
            {
                var contact = _contacts.FirstOrDefault(c => c.Id == info.UserId);
                if (contact != null) contact.Name = info.UserName;
            }
        });
    }

    private void OnHistoryResponse(HistoryResponsePayload history)
    {
        Dispatcher.Invoke(() =>
        {
            int contactId = history.ContactId;
            if (!_allDialogs.ContainsKey(contactId))
                _allDialogs[contactId] = new ObservableCollection<DialogMessage>();

            var existingIds = new HashSet<string>(_allDialogs[contactId].Select(m => m.Id));
            foreach (var hm in history.Messages)
            {
                if (existingIds.Contains(hm.MessageId)) continue;

                byte[]? sessionKey = null;
                if (hm.SenderId == _clientId)
                {
                    sessionKey = _dialogManager.GetSessionKey(contactId);
                    if (sessionKey == null)
                    {
                        // === ИЗМЕНЕНИЕ: пытаемся восстановить сессионный ключ из EncryptedKeySelf ===
                        if (hm.EncryptedKeySelf != null)
                        {
                            byte[] encKeySelf = Convert.FromBase64String(hm.EncryptedKeySelf);
                            byte[] decKey = _crypto.RsaDecrypt(_privateKeyXml, encKeySelf);
                            _dialogManager.SetSessionKey(contactId, decKey);
                            sessionKey = decKey;
                        }
                        else
                        {
                            continue;   // без ключа пропускаем
                        }
                    }
                }
                else if (hm.SenderId == contactId)
                {
                    if (hm.EncryptedKey != null)
                    {
                        byte[] encKey = Convert.FromBase64String(hm.EncryptedKey);
                        byte[] decKey = _crypto.RsaDecrypt(_privateKeyXml, encKey);
                        _dialogManager.SetSessionKey(contactId, decKey);
                        sessionKey = decKey;
                    }
                    else
                    {
                        sessionKey = _dialogManager.GetSessionKey(contactId);
                        if (sessionKey == null) continue;
                    }
                }
                else continue;

                if (hm.ContentType == "Text")
                {
                    byte[] ct = Convert.FromBase64String(hm.Ciphertext!);
                    byte[] n = Convert.FromBase64String(hm.Nonce!);
                    byte[] t = Convert.FromBase64String(hm.Tag!);
                    byte[] plain = _crypto.AesGcmDecrypt(sessionKey, n, ct, t);
                    var msg = new DialogMessage
                    {
                        Id = hm.MessageId,
                        SenderId = hm.SenderId,
                        ReceiverId = hm.ReceiverId,
                        Text = Encoding.UTF8.GetString(plain),
                        ContentType = "Text",
                        Status = hm.Status,
                        IsOutgoing = hm.SenderId == _clientId,
                        Timestamp = DateTime.Parse(hm.Timestamp)
                    };
                    _allDialogs[contactId].Add(msg);
                }
                else if (hm.ContentType == "File")
                {
                    var msg = new DialogMessage
                    {
                        Id = hm.MessageId,
                        SenderId = hm.SenderId,
                        ReceiverId = hm.ReceiverId,
                        FilePath = hm.FileName ?? "file",
                        ContentType = "File",
                        Status = hm.Status,
                        IsOutgoing = hm.SenderId == _clientId,
                        Timestamp = DateTime.Parse(hm.Timestamp)
                    };
                    _allDialogs[contactId].Add(msg);
                }
            }
            if (_currentContactId == contactId)
                MessagesListBox.ItemsSource = _allDialogs[contactId];
        });
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
                _contacts.Add(new ContactItem { Id = user.Id, Name = user.Name, IsOnline = user.IsOnline});
            }
            if (_contacts.Count > 0 && _currentContactId == -1)
                ContactsListBox.SelectedIndex = 0;
        });
    }

    private void OnDeliveryStatus(DeliveryStatusPayload status)
    {
        Dispatcher.Invoke(() =>
        {
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
            byte[]? sessionKey = _dialogManager.GetSessionKey(msg.SenderId);
            if (msg.EncryptedKey != null)
            {
                byte[] encKey = Convert.FromBase64String(msg.EncryptedKey);
                byte[] decKey = _crypto.RsaDecrypt(_privateKeyXml, encKey);
                _dialogManager.SetSessionKey(msg.SenderId, decKey);
                sessionKey = decKey;
            }
            if (sessionKey == null) return;

            if (msg.ContentType == "Text")
            {
                byte[] ct = Convert.FromBase64String(msg.EncryptedData.Ciphertext);
                byte[] n = Convert.FromBase64String(msg.EncryptedData.Nonce);
                byte[] t = Convert.FromBase64String(msg.EncryptedData.Tag);
                byte[] plain = _crypto.AesGcmDecrypt(sessionKey, n, ct, t);
                AddMessage(msg.SenderId, new DialogMessage
                {
                    Id = msg.MessageId,
                    SenderId = msg.SenderId,
                    ReceiverId = _clientId,
                    Text = Encoding.UTF8.GetString(plain),
                    ContentType = "Text",
                    Status = "✓✓",
                    IsOutgoing = false,
                    Timestamp = DateTime.Now
                });
                _ = _network.SendAsync(new { Type = "Ack", Payload = new AckPayload { MessageId = msg.MessageId, Status = "Delivered" } });
            }
            else if (msg.ContentType == "File")
            {
                _fileTransfer.HandleIncomingFile(msg, sessionKey, path =>
                {
                    Dispatcher.Invoke(() => AddMessage(msg.SenderId, new DialogMessage
                    {
                        Id = msg.MessageId,
                        SenderId = msg.SenderId,
                        ReceiverId = _clientId,
                        FilePath = path,
                        ContentType = "File",
                        Status = "✓✓",
                        IsOutgoing = false,
                        Timestamp = DateTime.Now
                    }));
                });
                _ = _network.SendAsync(new { Type = "Ack", Payload = new AckPayload { MessageId = msg.MessageId, Status = "Delivered" } });
            }
        });
    }

    private void OnAddContactResponse(AddContactResponsePayload response)
    {
        Dispatcher.Invoke(() =>
        {
            if (response.Status == "NotFound")
            {
                MessageBox.Show($"Пользователь с ID {response.ContactId} не найден на сервере.",
                                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                var contact = _contacts.FirstOrDefault(c => c.Id == response.ContactId);
                if (contact != null)
                {
                    _contacts.Remove(contact);
                    if (_allDialogs.ContainsKey(response.ContactId))
                        _allDialogs.Remove(response.ContactId);
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
            {
                _contacts.Add(new ContactItem { Id = contactId, Name = $"User {contactId}" });
            }
        }
        _allDialogs[contactId].Add(msg);
        if (_currentContactId == contactId)
            MessagesListBox.ItemsSource = _allDialogs[contactId];
    }

    // ---------- Кнопки и интерфейс ----------
    private async void NewDialogButton_Click(object sender, RoutedEventArgs e)
    {
        string input = RecipientIdTextBox.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            MessageBox.Show("Введите ID пользователя.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(input, out int id))
        {
            MessageBox.Show("ID должен быть числом.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (id == _clientId)
        {
            MessageBox.Show("Нельзя добавить самого себя.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_contacts.Any(c => c.Id == id))
        {
            MessageBox.Show("Контакт уже существует.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            ContactsListBox.SelectedItem = _contacts.First(c => c.Id == id);
            RecipientIdTextBox.Clear();
            return;
        }

        var newContact = new ContactItem { Id = id, Name = $"User {id}" };
        _contacts.Add(newContact);
        if (!_allDialogs.ContainsKey(id))
            _allDialogs[id] = new ObservableCollection<DialogMessage>();

        try { await _network.SendAsync(new { Type = "AddContact", Payload = new AddContactPayload { ContactId = id } }); }
        catch { }

        _ = RequestContactNameAsync(id);
        ContactsListBox.SelectedItem = newContact;
        RecipientIdTextBox.Clear();
    }

    private async Task RequestContactNameAsync(int userId)
    {
        try { await _network.SendAsync(new { Type = "GetUserInfo", Payload = new GetUserInfoPayload { TargetId = userId } }); }
        catch { }
    }

    private async void ContactsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContactsListBox.SelectedItem is ContactItem contact)
        {
            _currentContactId = contact.Id;
            if (!_allDialogs.ContainsKey(_currentContactId))
                _allDialogs[_currentContactId] = new ObservableCollection<DialogMessage>();
            MessagesListBox.ItemsSource = _allDialogs[_currentContactId];

            if (_allDialogs[_currentContactId].Count == 0)
            {
                try { await _network.SendAsync(new { Type = "GetHistory", Payload = new GetHistoryRequestPayload { ContactId = contact.Id } }); }
                catch { }
            }
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentContactId <= 0) { MessageBox.Show("Выберите получателя"); return; }
        string text = MessageTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            byte[]? sessionKey = _dialogManager.GetSessionKey(_currentContactId);
            bool firstMsg = sessionKey == null;
            string? encryptedKeyBase64 = null;
            string? encryptedKeySelfBase64 = null; // === ИЗМЕНЕНИЕ ===

            if (firstMsg)
            {
                var tcs = new TaskCompletionSource<string>();
                void handler(PublicKeyResponsePayload p) { if (p.TargetId == _currentContactId) tcs.TrySetResult(p.PublicKey); }
                _network.OnPublicKeyResponse += handler;
                await _network.SendAsync(new { Type = "GetPublicKey", Payload = new GetPublicKeyPayload { TargetId = _currentContactId } });
                var delayTask = Task.Delay(5000);
                var completed = await Task.WhenAny(tcs.Task, delayTask);
                _network.OnPublicKeyResponse -= handler;
                if (completed == delayTask)
                {
                    MessageBox.Show("Не удалось получить открытый ключ собеседника (таймаут).");
                    return;
                }
                string pubKey = await tcs.Task;

                sessionKey = _dialogManager.GetOrCreateSessionKey(_currentContactId);
                byte[] encKey = _crypto.RsaEncrypt(pubKey, sessionKey);
                encryptedKeyBase64 = Convert.ToBase64String(encKey);

                // === ИЗМЕНЕНИЕ: шифруем ключ и для себя ===
                byte[] encKeySelf = _crypto.RsaEncrypt(_publicKeyXml, sessionKey);
                encryptedKeySelfBase64 = Convert.ToBase64String(encKeySelf);
            }

            byte[] plain = Encoding.UTF8.GetBytes(text);
            var (ct, nonce, tag) = _crypto.AesGcmEncrypt(sessionKey!, plain);
            var msgId = Guid.NewGuid().ToString();

            await _network.SendAsync(new
            {
                Type = "SendMessage",
                Payload = new SendMessagePayload
                {
                    ReceiverId = _currentContactId,
                    MessageId = msgId,
                    EncryptedKey = encryptedKeyBase64,
                    EncryptedKeySelf = encryptedKeySelfBase64,   // === ИЗМЕНЕНИЕ ===
                    EncryptedData = new EncryptedData
                    {
                        Nonce = Convert.ToBase64String(nonce),
                        Ciphertext = Convert.ToBase64String(ct),
                        Tag = Convert.ToBase64String(tag)
                    },
                    ContentType = "Text"
                }
            });

            AddMessage(_currentContactId, new DialogMessage
            {
                Id = msgId,
                SenderId = _clientId,
                ReceiverId = _currentContactId,
                Text = text,
                ContentType = "Text",
                Status = "Отправка...",
                IsOutgoing = true,
                Timestamp = DateTime.Now
            });
            MessageTextBox.Clear();
        }
        catch (Exception ex) { MessageBox.Show($"Не удалось отправить сообщение: {ex.Message}"); }
    }

    private async void AttachFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentContactId <= 0) { MessageBox.Show("Выберите получателя"); return; }
        try
        {
            byte[]? sessionKey = _dialogManager.GetSessionKey(_currentContactId);
            bool firstMsg = sessionKey == null;
            string? encryptedKeyBase64 = null;
            string? encryptedKeySelfBase64 = null; // === ИЗМЕНЕНИЕ ===

            if (firstMsg)
            {
                var tcs = new TaskCompletionSource<string>();
                void handler(PublicKeyResponsePayload p) { if (p.TargetId == _currentContactId) tcs.TrySetResult(p.PublicKey); }
                _network.OnPublicKeyResponse += handler;
                await _network.SendAsync(new { Type = "GetPublicKey", Payload = new GetPublicKeyPayload { TargetId = _currentContactId } });
                var delayTask = Task.Delay(5000);
                var completed = await Task.WhenAny(tcs.Task, delayTask);
                _network.OnPublicKeyResponse -= handler;
                if (completed == delayTask) { MessageBox.Show("Таймаут получения ключа."); return; }
                string pubKey = await tcs.Task;

                sessionKey = _dialogManager.GetOrCreateSessionKey(_currentContactId);
                byte[] encKey = _crypto.RsaEncrypt(pubKey, sessionKey);
                encryptedKeyBase64 = Convert.ToBase64String(encKey);

                // === ИЗМЕНЕНИЕ: шифруем ключ и для себя ===
                byte[] encKeySelf = _crypto.RsaEncrypt(_publicKeyXml, sessionKey);
                encryptedKeySelfBase64 = Convert.ToBase64String(encKeySelf);
            }

            var dlg = new Microsoft.Win32.OpenFileDialog();
            if (dlg.ShowDialog() == true)
            {
                byte[] fileData = File.ReadAllBytes(dlg.FileName);
                var (ciphertext, nonce, tag) = _crypto.AesGcmEncrypt(sessionKey!, fileData);
                string transferId = Guid.NewGuid().ToString();
                var msgId = Guid.NewGuid().ToString();

                await _network.SendAsync(new
                {
                    Type = "SendMessage",
                    Payload = new SendMessagePayload
                    {
                        ReceiverId = _currentContactId,
                        MessageId = msgId,
                        EncryptedKey = encryptedKeyBase64,
                        EncryptedKeySelf = encryptedKeySelfBase64,   // === ИЗМЕНЕНИЕ ===
                        EncryptedData = new EncryptedData
                        {
                            Nonce = Convert.ToBase64String(nonce),
                            Ciphertext = Convert.ToBase64String(ciphertext),
                            Tag = Convert.ToBase64String(tag)
                        },
                        ContentType = "File",
                        FileName = Path.GetFileName(dlg.FileName),
                        FileSize = fileData.Length,
                        FileTransferId = transferId
                    }
                });

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
        catch (Exception ex) { MessageBox.Show($"Не удалось отправить файл: {ex.Message}"); }
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
        string current = Settings.Default.Theme;
        string newTheme = current == "Dark" ? "Light" : "Dark";
        App.ApplyTheme(newTheme);
        Settings.Default.Theme = newTheme;
        Settings.Default.Save();
    }

    private void ChangeName_Click(object sender, RoutedEventArgs e)
    {
        ChangeName_Execute();
    }

    private async void ChangeName_Execute()
    {
        string? newName = ShowInputDialog("Имя пользователя", _userName);
        if (!string.IsNullOrWhiteSpace(newName) && newName != _userName)
        {
            _userName = newName.Trim();
            SaveConfig();
            UpdateTitleAndStatus();
            try { await _network.SendAsync(new { Type = "UpdateProfile", Payload = new { UserName = _userName } }); }
            catch (Exception ex) { MessageBox.Show($"Не удалось обновить имя на сервере: {ex.Message}"); }
        }
    }

    private async void ChangeServerAddress_Click(object sender, RoutedEventArgs e)
    {
        string? newAddress = ShowInputDialog("Адрес сервера (ip:port)", _serverAddress);
        if (string.IsNullOrWhiteSpace(newAddress)) return;

        _serverAddress = newAddress.Trim();
        SaveConfig();

        try
        {
            _network.Disconnect();
            var parts = _serverAddress.Split(':');
            await _network.ConnectAsync(parts[0], int.Parse(parts[1]));
            await _network.SendAsync(new { Type = "Hello", Payload = new HelloPayload { ClientId = _clientId } });
            await RequestContactsAsync();
            UpdateTitleAndStatus();
            MessageBox.Show("Подключение к серверу выполнено успешно.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось подключиться к серверу {_serverAddress}:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _network.Disconnect();
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
        if (this.IsLoaded && this.IsVisible) dialog.Owner = this;

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
