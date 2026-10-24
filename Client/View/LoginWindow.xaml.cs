using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using SecureChat.Client.Services;
using SecureChat.Common;

namespace SecureChat.Client.View;

public partial class LoginWindow : Window
{
    // Данные успешной авторизации, передаются в MainWindow
    public int AuthenticatedUserId { get; private set; }
    public string AuthenticatedUserName { get; private set; } = "User";
    public string Token { get; private set; } = "";
    public string ServerUrl { get; private set; } = "ws://localhost:9000/";
    public string PublicKeyPem { get; private set; } = "";
    public string PrivateKeyPem { get; private set; } = "";
    public NetworkService Network { get; private set; } = new();

    private CryptoService _crypto = new();
    private TaskCompletionSource<LoginResponsePayload>? _loginTcs;      // Ожидание ответа на логин
    private TaskCompletionSource<RegisterAccountResponsePayload>? _registerTcs; // Ожидание ответа на регистрацию

    public LoginWindow()
    {
        InitializeComponent();
        // Подписка на ответы сервера: резолвим ожидающие TCS
        Network.OnLoginResponse += p => _loginTcs?.TrySetResult(p);
        Network.OnRegisterAccountResponse += p => _registerTcs?.TrySetResult(p);
        Network.OnError += err => Dispatcher.Invoke(() => StatusTextBlock.Text = $"Ошибка: {err}");

        LoadSavedConfig();
    }

    // Загрузка сохранённых настроек из %AppData%\SecureChat\config.json
    private void LoadSavedConfig()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecureChat");
            string configFile = Path.Combine(dir, "config.json");
            if (File.Exists(configFile))
            {
                var cfg = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(configFile));
                if (cfg != null)
                {
                    if (cfg.TryGetValue("ServerAddress", out var srv) && !string.IsNullOrWhiteSpace(srv))
                        ServerUrlBox.Text = srv;
                    if (cfg.TryGetValue("Login", out var log) && !string.IsNullOrWhiteSpace(log))
                        LoginBox.Text = log;
                    if (cfg.TryGetValue("PublicKey", out var pub)) PublicKeyPem = pub;
                    if (cfg.TryGetValue("PrivateKey", out var priv)) PrivateKeyPem = priv;
                }
            }
        }
        catch { }
    }

    // Сохранение текущей конфигурации (сессия + ключи)
    private void SaveConfig(string login)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecureChat");
            Directory.CreateDirectory(dir);
            var cfg = new System.Collections.Generic.Dictionary<string, string>
            {
                ["ClientId"] = AuthenticatedUserId.ToString(),
                ["UserName"] = AuthenticatedUserName,
                ["Login"] = login,
                ["ServerAddress"] = ServerUrl,
                ["Token"] = Token,
                ["PublicKey"] = PublicKeyPem,
                ["PrivateKey"] = PrivateKeyPem,
                ["Theme"] = App.CurrentTheme
            };
            File.WriteAllText(Path.Combine(dir, "config.json"), JsonSerializer.Serialize(cfg));
        }
        catch { }
    }

    // Переключение режима Вход / Регистрация
    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (NamePanel == null || SubmitButton == null) return;
        bool isRegister = RegisterRadio.IsChecked == true;
        NamePanel.Visibility = isRegister ? Visibility.Visible : Visibility.Collapsed;
        SubmitButton.Content = isRegister ? "Зарегистрироваться" : "Войти";
        StatusTextBlock.Text = "";
    }

    // Основная логика: подключение, отправка запроса, ожидание ответа
    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        string login = LoginBox.Text.Trim();
        string password = PasswordBox.Password;
        string serverUrl = ServerUrlBox.Text.Trim();

        if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
        {
            StatusTextBlock.Text = "Заполните логин и пароль.";
            return;
        }

        SubmitButton.IsEnabled = false;
        StatusTextBlock.Text = "Подключение к серверу...";

        try
        {
            // Ленивое подключение, если ещё не подключены
            if (!Network.IsConnected)
            {
                await Network.ConnectAsync(serverUrl);
            }

            ServerUrl = serverUrl;

            if (RegisterRadio.IsChecked == true)
            {
                // ---- Регистрация ----
                string name = NameBox.Text.Trim();

                // Ключевая пара генерируется один раз и переиспользуется
                if (string.IsNullOrEmpty(PublicKeyPem) || string.IsNullOrEmpty(PrivateKeyPem))
                {
                    var keys = _crypto.GenerateRsaKeyPair();
                    PublicKeyPem = keys.publicKey;
                    PrivateKeyPem = keys.privateKey;
                }

                _registerTcs = new TaskCompletionSource<RegisterAccountResponsePayload>();
                await Network.SendAsync(new
                {
                    Type = "RegisterAccount",
                    Payload = new RegisterAccountPayload
                    {
                        Login = login,
                        Password = password,
                        UserName = string.IsNullOrEmpty(name) ? login : name,
                        PublicKey = PublicKeyPem
                    }
                });

                // Таймаут 5 секунд на ответ
                var completed = await Task.WhenAny(_registerTcs.Task, Task.Delay(5000));
                if (completed != _registerTcs.Task)
                {
                    StatusTextBlock.Text = "Таймаут регистрации на сервере.";
                    SubmitButton.IsEnabled = true;
                    return;
                }

                var resp = await _registerTcs.Task;
                if (resp.Status == "Ok")
                {
                    AuthenticatedUserId = resp.AssignedId;
                    AuthenticatedUserName = string.IsNullOrEmpty(name) ? login : name;
                    Token = resp.Token;
                    SaveConfig(login);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    StatusTextBlock.Text = resp.Status == "UserExists"
                        ? "Пользователь с таким логином уже существует."
                        : $"Ошибка регистрации: {resp.Status}";
                    SubmitButton.IsEnabled = true;
                }
            }
            else
            {
                // ---- Вход ----
                _loginTcs = new TaskCompletionSource<LoginResponsePayload>();
                await Network.SendAsync(new
                {
                    Type = "Login",
                    Payload = new LoginPayload
                    {
                        Login = login,
                        Password = password
                    }
                });

                var completed = await Task.WhenAny(_loginTcs.Task, Task.Delay(5000));
                if (completed != _loginTcs.Task)
                {
                    StatusTextBlock.Text = "Таймаут авторизации.";
                    SubmitButton.IsEnabled = true;
                    return;
                }

                var resp = await _loginTcs.Task;
                if (resp.Status == "Ok")
                {
                    AuthenticatedUserId = resp.UserId;
                    AuthenticatedUserName = resp.UserName;
                    Token = resp.Token;

                    // Ключи создаются при первом входе, если ещё не сохранены
                    if (string.IsNullOrEmpty(PublicKeyPem) || string.IsNullOrEmpty(PrivateKeyPem))
                    {
                        var keys = _crypto.GenerateRsaKeyPair();
                        PublicKeyPem = keys.publicKey;
                        PrivateKeyPem = keys.privateKey;
                    }

                    SaveConfig(login);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    StatusTextBlock.Text = resp.Status == "InvalidCredentials"
                        ? "Неверный логин или пароль."
                        : "Пользователь не найден.";
                    SubmitButton.IsEnabled = true;
                }
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Ошибка: {ex.Message}";
            SubmitButton.IsEnabled = true;
        }
    }
}