using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

    private int _currentContactId = -1;
    private Dictionary<int, string> _contactPublicKeys = new();

    public MainWindow()
    {
        InitializeComponent();
        _userName = "User";
        _token = "";
        _serverAddress = LoginWindow.StaticServerUrl;
        _publicKeyPem = "";
        _privateKeyPem = "";
        _network = new NetworkService();
        _dialogManager = new DialogManager(_crypto);
        _fileTransfer = new FileTransferManager(_crypto, _network);
        InitCommon();
    }

    public MainWindow(int clientId, string userName, string token, string serverAddress, string publicKeyPem, string privateKeyPem, NetworkService network)
    {
        InitializeComponent();
        _clientId = clientId;
        _userName = userName;
        _token = token;
        _serverAddress = serverAddress;
        _publicKeyPem = publicKeyPem;
        _privateKeyPem = privateKeyPem;
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
            Dispatcher.Invoke(() => MessageBox.Show($"Ошибка: {err}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error));
        };
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

        UpdateTitleAndStatus();
        Log.Information("MainWindow открыт userId={UserId}", _clientId);

        this.Loaded += async (s, e) => { await _network.GetContactsAsync(); };
    }

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
            await _network.HelloAsync(new HelloPayload { ClientId = _clientId, Token = _token });
            await _network.GetContactsAsync();
            UpdateTitleAndStatus();
            MessageBox.Show("Переподключение выполнено успешно.", "Сервер", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка переподключения");
            MessageBox.Show($"Ошибка переподключения: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SwitchAccount_Click(object sender, RoutedEventArgs e)
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
                    loginWindow.PublicKeyPem, loginWindow.PrivateKeyPem, loginWindow.Network);
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
            Log.Error(ex, "Ошибка смены учётной записи");
            app.Shutdown();
            throw;
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
                existingIds.Add(hm.MessageId);
            }

            Log.Debug("HistoryResponse contactId={ContactId} count={Count}", contactId, history.Messages.Count);

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
                _contacts.Add(new ContactItem { Id = user.Id, RawName = user.Name, IsOnline = user.IsOnline });
            }
            if (_contacts.Count > 0 && _currentContactId == -1)
                ContactsListBox.SelectedIndex = 0;
            Log.Debug("ContactList получен count={Count}", list.Contacts.Count);
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
                        Log.Error(ex, "Ошибка приёма файла messageId={MessageId}", messageId);
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
            _currentContactId = contact.Id;
            if (!_allDialogs.ContainsKey(_currentContactId))
                _allDialogs[_currentContactId] = new ObservableCollection<DialogMessage>();
            MessagesListBox.ItemsSource = _allDialogs[_currentContactId];

            if (_allDialogs[_currentContactId].Count == 0)
                await _network.GetHistoryAsync(new GetHistoryRequestPayload { ContactId = contact.Id });
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentContactId <= 0) { MessageBox.Show("Выберите получателя", "Информация", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string text = MessageTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

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
                ContentType = "Text"
            });

            Log.Information("Message sent to userId={ReceiverId} length={Length} messageId={MessageId}",
                _currentContactId, text.Length, msgId);

            MessageTextBox.Clear();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Не удалось отправить сообщение contactId={ContactId}", _currentContactId);
            MessageBox.Show($"Не удалось отправить сообщение: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AttachFileButton_Click(object sender, RoutedEventArgs e)
    {
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
            Log.Error(ex, "Не удалось отправить файл contactId={ContactId}", _currentContactId);
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
        Log.Information("Тема изменена на {Theme}", newTheme);
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
        Log.Information("Закрытие MainWindow userId={UserId}", _clientId);
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