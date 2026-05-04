using SecureChat.Client.Models;
using SecureChat.Client.Properties;
using SecureChat.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    private ObservableCollection<DialogMessage>? _currentMessages;
    private Dictionary<int, ObservableCollection<DialogMessage>> _allDialogs = new();
    private TaskCompletionSource<int>? _registrationTcs;

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
        _network.OnUserList += OnUserList;

        LoadConfigOrConnect();
    }

    private async void LoadConfigOrConnect()
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
                    break;
                }
                catch (SocketException)
                {
                    var result = MessageBox.Show(
                        $"Не удалось подключиться к серверу {_serverAddress}.\nПовторить попытку?",
                        "Ошибка подключения",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (result == MessageBoxResult.No)
                    {
                        Application.Current.Shutdown();
                        return;
                    }
                }
            }
        }
        else
        {
            string? discovered = null;
            try
            {
                discovered = await DiscoverServerAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка поиска сервера: {ex.Message}\nБудет использован localhost.");
            }

            _serverAddress = discovered ?? "127.0.0.1:9000";
            if (discovered == null)
                MessageBox.Show("Сервер не найден, используется localhost.");

            var keys = _crypto.GenerateRsaKeyPair();
            _publicKeyXml = keys.publicKey;
            _privateKeyXml = keys.privateKey;

            while (true)
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
                    break;
                }
                catch (SocketException)
                {
                    var result = MessageBox.Show(
                        $"Не удалось подключиться к серверу {_serverAddress}.\nПовторить попытку?",
                        "Ошибка подключения",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (result == MessageBoxResult.No)
                    {
                        Application.Current.Shutdown();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Критическая ошибка при регистрации: {ex.Message}");
                    Application.Current.Shutdown();
                    return;
                }
            }
        }
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

    private async Task<string?> DiscoverServerAsync()
    {
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        byte[] req = Encoding.UTF8.GetBytes("DISCOVER_SECURECHAT");
        await udp.SendAsync(req, req.Length, new IPEndPoint(IPAddress.Broadcast, 9001));
        var timeout = Task.Delay(2000);
        while (!timeout.IsCompleted)
        {
            if (udp.Available > 0)
            {
                var result = await udp.ReceiveAsync();
                string resp = Encoding.UTF8.GetString(result.Buffer);
                if (resp.StartsWith("SECURECHAT_SERVER|"))
                    return resp.Substring("SECURECHAT_SERVER|".Length);
            }
            await Task.Delay(100);
        }
        return null;
    }

    private void UpdateConnectionStatus(bool connected)
    {
        ConnectionIndicator.Fill = connected ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Gray);
        UserIdTextBlock.Text = connected ? $"{_userName} (ID: {_clientId})" : $"{_userName} (ID: {_clientId}) (отключено)";
    }

    private void OnUserList(UserListPayload users)
    {
        Dispatcher.Invoke(() =>
        {
            ContactsListBox.Items.Clear();
            foreach (var id in users.Users)
            {
                if (!_allDialogs.ContainsKey(id))
                    _allDialogs[id] = new ObservableCollection<DialogMessage>();
                ContactsListBox.Items.Add(new KeyValuePair<int, string>(id, ""));
            }
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
                string text = Encoding.UTF8.GetString(plain);
                AddMessage(msg.SenderId, new DialogMessage
                {
                    Id = msg.MessageId,
                    SenderId = msg.SenderId,
                    ReceiverId = _clientId,
                    Text = text,
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
                        FilePath = path,      // полный путь, конвертер выделит имя
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

    private void AddMessage(int contactId, DialogMessage msg)
    {
        if (!_allDialogs.ContainsKey(contactId))
        {
            _allDialogs[contactId] = new ObservableCollection<DialogMessage>();
            ContactsListBox.Items.Add(new KeyValuePair<int, string>(contactId, $"User {contactId}"));
        }
        _allDialogs[contactId].Add(msg);
        if (_currentContactId == contactId)
            MessagesListBox.ItemsSource = _allDialogs[contactId];
    }

    private void NewDialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(RecipientIdTextBox.Text, out int id) && id != _clientId)
        {
            if (!_allDialogs.ContainsKey(id))
            {
                _allDialogs[id] = new ObservableCollection<DialogMessage>();
                ContactsListBox.Items.Add(new KeyValuePair<int, string>(id, $"User {id}"));
            }
        }
    }

    private void ContactsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContactsListBox.SelectedItem is KeyValuePair<int, string> pair)
        {
            _currentContactId = pair.Key;
            MessagesListBox.ItemsSource = _allDialogs[_currentContactId];
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

            if (firstMsg)
            {
                var tcs = new TaskCompletionSource<string>();
                void handler(PublicKeyResponsePayload p) { if (p.TargetId == _currentContactId) tcs.TrySetResult(p.PublicKey); }
                _network.OnPublicKeyResponse += handler;
                await _network.SendAsync(new { Type = "GetPublicKey", Payload = new GetPublicKeyPayload { TargetId = _currentContactId } });
                string pubKey = await tcs.Task;
                _network.OnPublicKeyResponse -= handler;

                sessionKey = _dialogManager.GetOrCreateSessionKey(_currentContactId);
                byte[] encKey = _crypto.RsaEncrypt(pubKey, sessionKey);
                encryptedKeyBase64 = Convert.ToBase64String(encKey);
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
        if (_currentContactId <= 0) { MessageBox.Show("Выберите получателя"); return; }
        try
        {
            byte[]? sessionKey = _dialogManager.GetSessionKey(_currentContactId);
            bool firstMsg = sessionKey == null;
            string? encryptedKeyBase64 = null;

            if (firstMsg)
            {
                var tcs = new TaskCompletionSource<string>();
                void handler(PublicKeyResponsePayload p) { if (p.TargetId == _currentContactId) tcs.TrySetResult(p.PublicKey); }
                _network.OnPublicKeyResponse += handler;
                await _network.SendAsync(new { Type = "GetPublicKey", Payload = new GetPublicKeyPayload { TargetId = _currentContactId } });
                string pubKey = await tcs.Task;
                _network.OnPublicKeyResponse -= handler;

                sessionKey = _dialogManager.GetOrCreateSessionKey(_currentContactId);
                byte[] encKey = _crypto.RsaEncrypt(pubKey, sessionKey);
                encryptedKeyBase64 = Convert.ToBase64String(encKey);
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
                    FilePath = Path.GetFileName(dlg.FileName),   // только имя файла
                    ContentType = "File",
                    Status = "✓",
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

    private async void RefreshUsers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _network.SendAsync(new { Type = "GetUsers" });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось обновить список пользователей: {ex.Message}");
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

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _network.Disconnect();
    }

    private void ExitMenu_Click(object sender, RoutedEventArgs e)
    {
        Window_Closing(sender, null);
        Application.Current.Shutdown();
    }

    private void ToggleThemeMenu_Click(object sender, RoutedEventArgs e)
    {
        ToggleTheme_Click(sender, e);
    }

    private void ChangeNameMenu_Click(object sender, RoutedEventArgs e)
    {
        string newName = ShowInputDialog("Имя пользователя", _userName);
        if (!string.IsNullOrWhiteSpace(newName) && newName != _userName)
        {
            _userName = newName.Trim();
            SaveConfig();
            UpdateTitleAndStatus();
        }
    }

    private string? ShowInputDialog(string title, string defaultText)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 320,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = this.Background,
            Foreground = this.Foreground
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var textBox = new TextBox
        {
            Text = defaultText,
            Margin = new Thickness(8),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(textBox, 0);
        grid.Children.Add(textBox);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8)
        };
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