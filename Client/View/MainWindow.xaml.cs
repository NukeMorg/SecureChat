using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SecureChat.Client.Models;
using SecureChat.Client.Services;
using SecureChat.Client.View;
using SecureChat.Common;

namespace SecureChat.Client;

public partial class MainWindow : Window
{
    // Объявление статических команд для XAML {x:Static local:MainWindow.*}
    public static readonly RoutedUICommand ExitCommand = new(
        "Выход", nameof(ExitCommand), typeof(MainWindow));

    public static readonly RoutedUICommand ToggleThemeCommand = new(
        "Сменить тему", nameof(ToggleThemeCommand), typeof(MainWindow));

    public static readonly RoutedUICommand ChangeNameCommand = new(
        "Изменить имя", nameof(ChangeNameCommand), typeof(MainWindow));

    public static readonly RoutedUICommand ChangeServerAddressCommand = new(
        "Указать адрес сервера", nameof(ChangeServerAddressCommand), typeof(MainWindow));

    // Коллекции данных интерфейса
    private readonly ObservableCollection<ContactItem> _contacts = new();
    private readonly Dictionary<int, ObservableCollection<DialogMessage>> _allDialogs = new();

    private NetworkService _network;              // Сетевое соединение
    private CryptoService _crypto = new();        // Шифрование RSA + AES-GCM
    private DialogManager _dialogManager;         // Сессионные ключи диалогов
    private FileTransferManager _fileTransfer;    // Отправка/приём файлов

    // Данные сессии
    private int _clientId;
    private string _userName;
    private string _token;
    private string _serverAddress;
    private string _publicKeyPem;
    private string _privateKeyPem;

    private int _currentContactId = -1;                     // Активный собеседник
    private Dictionary<int, string> _contactPublicKeys = new(); // Кэш публичных ключей контактов

    // Конструктор по умолчанию (дизайнер / fallback)
    public MainWindow()
    {
        InitializeComponent();
        _userName = "User";
        _token = "";
        _serverAddress = "ws://localhost:9000/";
        _publicKeyPem = "";
        _privateKeyPem = "";
        _network = new NetworkService();
        _dialogManager = new DialogManager(_crypto);
        _fileTransfer = new FileTransferManager(_crypto, _network);

        // Привязка команд меню / горячих клавиш
        CommandBindings.Add(new CommandBinding(ExitCommand, (s, e) => Application.Current.Shutdown()));
        CommandBindings.Add(new CommandBinding(ToggleThemeCommand, (s, e) => ToggleTheme_Click(s, e)));
        CommandBindings.Add(new CommandBinding(ChangeNameCommand, (s, e) => ChangeName_Click(s, e)));
        CommandBindings.Add(new CommandBinding(ChangeServerAddressCommand, (s, e) => ChangeServerAddress_Click(s, e)));

        ContactsListBox.ItemsSource = _contacts;
    }

    // Основной конструктор с данными авторизованной сессии
    public MainWindow(int clientId, string userName, string token, string serverAddress, string publicKeyPem, string privateKeyPem, NetworkService network)
        : this()
    {
        _clientId = clientId;
        _userName = userName;
        _token = token;
        _serverAddress = serverAddress;
        _publicKeyPem = publicKeyPem;
        _privateKeyPem = privateKeyPem;
        _network = network;

        _fileTransfer = new FileTransferManager(_crypto, _network);

        // Подписка на события сети
        _network.OnIncomingMessage += OnIncomingMessage;
        _network.OnDeliveryStatus += OnDeliveryStatus;
        _network.OnConnected += () => Dispatcher.Invoke(() => UpdateConnectionStatus(true));
        _network.OnDisconnected += () => Dispatcher.Invoke(() => UpdateConnectionStatus(false));
        _network.OnError += err => Dispatcher.Invoke(() => MessageBox.Show($"Ошибка: {err}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error));
        _network.OnUserProfileUpdated += OnUserProfileUpdated;
        _network.OnUserInfoResponse += OnUserInfoResponse;
        _network.OnHistoryResponse += OnHistoryResponse;
        _network.OnContactList += OnContactList;
        _network.OnAddContactResponse += OnAddContactResponse;

        // Изменение онлайн-статуса контактов
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

        // После загрузки запрашиваем список контактов
        this.Loaded += async (s, e) =>
        {
            await _network.SendAsync(new { Type = "GetContacts" });
        };
    }

    private string? GetContactPublicKey(int contactId)
    {
        _contactPublicKeys.TryGetValue(contactId, out var key);
        return key;
    }

    private void SetContactPublicKey(int contactId, string key)
    {
        _contactPublicKeys[contactId] = key;
    }

    private void UpdateTitleAndStatus()
    {
        Title = $"SecureChat Enterprise - {_userName} (ID: {_clientId})";
        UserIdTextBlock.Text = $"{_userName} (ID: {_clientId})";
        ConnectionIndicator.Fill = _network.IsConnected ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Gray);
    }

    private void UpdateConnectionStatus(bool connected)
    {
        ConnectionIndicator.Fill = connected ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Gray);
        UserIdTextBlock.Text = connected ? $"{_userName} (ID: {_clientId})" : $"{_userName} (ID: {_clientId}) (отключено)";
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _network.ConnectAsync(_serverAddress);
            await _network.SendAsync(new { Type = "Hello", Payload = new HelloPayload { ClientId = _clientId, Token = _token } });
            await _network.SendAsync(new { Type = "GetContacts" });
            UpdateTitleAndStatus();
            MessageBox.Show("Переподключение выполнено успешно.", "Сервер", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка переподключения: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        _network.Disconnect();
        var loginWindow = new LoginWindow();
        bool? res = loginWindow.ShowDialog();
        if (res == true && loginWindow.AuthenticatedUserId > 0)
        {
            var nextMain = new MainWindow(
                loginWindow.AuthenticatedUserId,
                loginWindow.AuthenticatedUserName,
                loginWindow.Token,
                loginWindow.ServerUrl,
                loginWindow.PublicKeyPem,
                loginWindow.PrivateKeyPem,
                loginWindow.Network
            );
            nextMain.Show();
            Close();
        }
    }

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

                if (hm.ContentType == "Text" && hm.Ciphertext != null && hm.Nonce != null && hm.Tag != null)
                {
                    byte[] ct = Convert.FromBase64String(hm.Ciphertext);
                    byte[] n = Convert.FromBase64String(hm.Nonce);
                    byte[] t = Convert.FromBase64String(hm.Tag);
                    byte[] plain = _crypto.AesGcmDecrypt(sessionKey, n, ct, t);
                    _allDialogs[contactId].Add(new DialogMessage
                    {
                        Id = hm.MessageId,
                        SenderId = hm.SenderId,
                        ReceiverId = hm.ReceiverId,
                        Text = Encoding.UTF8.GetString(plain),
                        ContentType = "Text",
                        Status = hm.Status,
                        IsOutgoing = hm.SenderId == _clientId,
                        Timestamp = DateTime.Parse(hm.Timestamp)
                    });
                }
                else if (hm.ContentType == "File")
                {
                    _allDialogs[contactId].Add(new DialogMessage
                    {
                        Id = hm.MessageId,
                        SenderId = hm.SenderId,
                        ReceiverId = hm.ReceiverId,
                        FilePath = hm.FileName ?? "file",
                        ContentType = "File",
                        Status = hm.Status,
                        IsOutgoing = hm.SenderId == _clientId,
                        Timestamp = DateTime.Parse(hm.Timestamp)
                    });
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
                _contacts.Add(new ContactItem { Id = user.Id, Name = user.Name, IsOnline = user.IsOnline });
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
                MessageBox.Show($"Сотрудник с ID {response.ContactId} не найден на сервере.",
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
                _contacts.Add(new ContactItem { Id = contactId, Name = $"User {contactId}", IsOnline = true });
            }
        }
        _allDialogs[contactId].Add(msg);
        if (_currentContactId == contactId)
            MessagesListBox.ItemsSource = _allDialogs[contactId];
    }

    private async void NewDialogButton_Click(object sender, RoutedEventArgs e)
    {
        string input = RecipientIdTextBox.Text.Trim();
        if (string.IsNullOrEmpty(input) || !int.TryParse(input, out int id) || id <= 0)
        {
            MessageBox.Show("Введите корректный числовой ID пользователя.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (id == _clientId)
        {
            MessageBox.Show("Нельзя добавить самого себя.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_contacts.Any(c => c.Id == id))
        {
            ContactsListBox.SelectedItem = _contacts.First(c => c.Id == id);
            RecipientIdTextBox.Clear();
            return;
        }

        var newContact = new ContactItem { Id = id, Name = $"User {id}", IsOnline = false };
        _contacts.Add(newContact);
        if (!_allDialogs.ContainsKey(id))
            _allDialogs[id] = new ObservableCollection<DialogMessage>();

        await _network.SendAsync(new { Type = "AddContact", Payload = new AddContactPayload { ContactId = id } });
        await _network.SendAsync(new { Type = "GetUserInfo", Payload = new GetUserInfoPayload { TargetId = id } });

        ContactsListBox.SelectedItem = newContact;
        RecipientIdTextBox.Clear();
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
                await _network.SendAsync(new { Type = "GetHistory", Payload = new GetHistoryRequestPayload { ContactId = contact.Id } });
            }
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentContactId <= 0) { MessageBox.Show("Выберите получателя", "Информация", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string text = MessageTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            string? pubKey = GetContactPublicKey(_currentContactId);
            if (pubKey == null)
            {
                var tcs = new TaskCompletionSource<string>();
                void handler(PublicKeyResponsePayload p) { if (p.TargetId == _currentContactId && p.Status == "Ok") tcs.TrySetResult(p.PublicKey); }
                _network.OnPublicKeyResponse += handler;
                await _network.SendAsync(new { Type = "GetPublicKey", Payload = new GetPublicKeyPayload { TargetId = _currentContactId } });
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                _network.OnPublicKeyResponse -= handler;
                if (completed != tcs.Task)
                {
                    MessageBox.Show("Не удалось получить открытый ключ собеседника (таймаут).", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                pubKey = await tcs.Task;
                SetContactPublicKey(_currentContactId, pubKey);
            }

            byte[] sessionKey = _dialogManager.GetOrCreateSessionKey(_currentContactId);
            byte[] encKey = _crypto.RsaEncrypt(pubKey, sessionKey);
            byte[] encKeySelf = _crypto.RsaEncrypt(_publicKeyPem, sessionKey);

            byte[] plain = Encoding.UTF8.GetBytes(text);
            var (ct, nonce, tag) = _crypto.AesGcmEncrypt(sessionKey, plain);
            var msgId = Guid.NewGuid().ToString();

            await _network.SendAsync(new
            {
                Type = "SendMessage",
                Payload = new SendMessagePayload
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
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось отправить сообщение: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AttachFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentContactId <= 0) { MessageBox.Show("Выберите получателя", "Информация", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            string? pubKey = GetContactPublicKey(_currentContactId);
            if (pubKey == null)
            {
                var tcs = new TaskCompletionSource<string>();
                void handler(PublicKeyResponsePayload p) { if (p.TargetId == _currentContactId && p.Status == "Ok") tcs.TrySetResult(p.PublicKey); }
                _network.OnPublicKeyResponse += handler;
                await _network.SendAsync(new { Type = "GetPublicKey", Payload = new GetPublicKeyPayload { TargetId = _currentContactId } });
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                _network.OnPublicKeyResponse -= handler;
                if (completed != tcs.Task)
                {
                    MessageBox.Show("Таймаут получения открытого ключа.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                pubKey = await tcs.Task;
                SetContactPublicKey(_currentContactId, pubKey);
            }

            var dlg = new Microsoft.Win32.OpenFileDialog();
            if (dlg.ShowDialog() == true)
            {
                byte[] sessionKey = _dialogManager.GetOrCreateSessionKey(_currentContactId);
                byte[] encKey = _crypto.RsaEncrypt(pubKey, sessionKey);
                byte[] encKeySelf = _crypto.RsaEncrypt(_publicKeyPem, sessionKey);

                await _fileTransfer.SendFileAsync(
                    _currentContactId,
                    dlg.FileName,
                    sessionKey,
                    Convert.ToBase64String(encKey),
                    Convert.ToBase64String(encKeySelf)
                );

                AddMessage(_currentContactId, new DialogMessage
                {
                    Id = Guid.NewGuid().ToString(),
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
    }

    private async void ChangeName_Click(object sender, RoutedEventArgs e)
    {
        string? newName = ShowInputDialog("Имя пользователя", _userName);
        if (!string.IsNullOrWhiteSpace(newName) && newName != _userName)
        {
            _userName = newName.Trim();
            UpdateTitleAndStatus();
            await _network.SendAsync(new { Type = "UpdateProfile", Payload = new { UserName = _userName } });
        }
    }

    private async void ChangeServerAddress_Click(object sender, RoutedEventArgs e)
    {
        string? newAddr = ShowInputDialog("Адрес сервера WebSocket", _serverAddress);
        if (!string.IsNullOrWhiteSpace(newAddr) && newAddr != _serverAddress)
        {
            _serverAddress = newAddr.Trim();
            try
            {
                await _network.ConnectAsync(_serverAddress);
                await _network.SendAsync(new { Type = "Hello", Payload = new HelloPayload { ClientId = _clientId, Token = _token } });
                await _network.SendAsync(new { Type = "GetContacts" });
                UpdateTitleAndStatus();
                MessageBox.Show("Подключение к новому серверу выполнено успешно.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка подключения: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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