using System;
using System.IO;
using System.Net.Mail;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using SecureChat.Client.Services;
using SecureChat.Common;

namespace SecureChat.Client.View;

/// <summary>
/// Окно входа/регистрации: подключение к серверу, авторизация по email, сохранение конфига.
/// Работает с общим экземпляром NetworkService (переживает открытие/закрытие окна).
/// </summary>
public partial class LoginWindow : Window
{
    // Адрес сервера (замените на реальный IP при развёртывании)
    public const string StaticServerUrl = "ws://localhost:9000/";

    // Данные успешно аутентифицированного пользователя
    public int AuthenticatedUserId { get; private set; }
    public string AuthenticatedUserName { get; private set; } = "User";
    public string Token { get; private set; } = "";
    public string ServerUrl => StaticServerUrl;
    public string PublicKeyPem { get; private set; } = "";
    public string PrivateKeyPem { get; private set; } = "";
    public NetworkService Network { get; private set; }

    private CryptoService _crypto = new();
    // TCS для ожидания ответов сервера на запросы логина/регистрации
    private TaskCompletionSource<LoginResponsePayload>? _loginTcs;
    private TaskCompletionSource<RegisterAccountResponsePayload>? _registerTcs;

    // Ссылки на делегаты — нужны, чтобы отписаться от событий перед закрытием окна
    private readonly Action<LoginResponsePayload> _loginHandler;
    private readonly Action<RegisterAccountResponsePayload> _registerHandler;
    private readonly Action<string> _errorHandler;

    /// <summary>
    /// Конструктор для XAML-дизайнера — создаёт собственный NetworkService.
    /// </summary>
    public LoginWindow() : this(new NetworkService())
    {
    }

    /// <summary>
    /// Рабочий конструктор: использует общий (постоянный) NetworkService приложения.
    /// </summary>
    public LoginWindow(NetworkService network)
    {
        InitializeComponent();
        Network = network;

        // Сохраняем ссылки на делегаты для последующей отписки
        _loginHandler = p => _loginTcs?.TrySetResult(p);
        _registerHandler = p => _registerTcs?.TrySetResult(p);
        _errorHandler = err => Dispatcher.Invoke(() => StatusTextBlock.Text = $"Ошибка: {err}");

        Network.OnLoginResponse += _loginHandler;
        Network.OnRegisterAccountResponse += _registerHandler;
        Network.OnError += _errorHandler;

        LoadSavedConfig();
    }

    /// <summary>
    /// Отписывается от событий NetworkService, чтобы не тянуть ссылку на закрытое окно.
    /// </summary>
    private void UnsubscribeNetworkHandlers()
    {
        Network.OnLoginResponse -= _loginHandler;
        Network.OnRegisterAccountResponse -= _registerHandler;
        Network.OnError -= _errorHandler;
    }

    /// <summary>
    /// Загружает сохранённый email и ключи из %AppData%\SecureChat\config.json.
    /// </summary>
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
                    if (cfg.TryGetValue("Login", out var log) && !string.IsNullOrWhiteSpace(log))
                        LoginBox.Text = log;
                    if (cfg.TryGetValue("PublicKey", out var pub)) PublicKeyPem = pub;
                    if (cfg.TryGetValue("PrivateKey", out var priv)) PrivateKeyPem = priv;
                }
            }
        }
        catch { } // Игнорируем ошибки чтения конфига
    }

    /// <summary>
    /// Сохраняет текущие данные пользователя в config.json.
    /// </summary>
    private void SaveConfig(string email)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecureChat");
            Directory.CreateDirectory(dir);
            var cfg = new System.Collections.Generic.Dictionary<string, string>
            {
                ["ClientId"] = AuthenticatedUserId.ToString(),
                ["UserName"] = AuthenticatedUserName,
                ["Login"] = email,
                ["Token"] = Token,
                ["PublicKey"] = PublicKeyPem,
                ["PrivateKey"] = PrivateKeyPem,
                ["Theme"] = App.CurrentTheme
            };
            File.WriteAllText(Path.Combine(dir, "config.json"), JsonSerializer.Serialize(cfg));
        }
        catch { } // Игнорируем ошибки записи конфига
    }

    /// <summary>
    /// Переключение режимов "Вход" / "Регистрация".
    /// </summary>
    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (NamePanel == null || SubmitButton == null) return;
        bool isRegister = RegisterRadio.IsChecked == true;
        NamePanel.Visibility = isRegister ? Visibility.Visible : Visibility.Collapsed;
        SubmitButton.Content = isRegister ? "Зарегистрироваться" : "Войти";
        StatusTextBlock.Text = "";
    }

    /// <summary>
    /// Проверяет, что строка — корректный email (через MailAddress).
    /// </summary>
    private static bool IsValidEmail(string email)
    {
        try
        {
            var addr = new MailAddress(email);
            return addr.Address == email.Trim();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Обрабатывает нажатие кнопки: валидация, подключение, отправка запроса, ожидание ответа.
    /// </summary>
    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        string email = LoginBox.Text.Trim();
        string password = PasswordBox.Password;

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            StatusTextBlock.Text = "Заполните почту и пароль.";
            return;
        }

        // Локальная проверка email до обращения к серверу
        if (!IsValidEmail(email))
        {
            StatusTextBlock.Text = "Введите корректный Email.";
            return;
        }

        SubmitButton.IsEnabled = false;
        StatusTextBlock.Text = "Подключение к серверу...";

        try
        {
            // Подключаемся, если ещё нет соединения
            if (!Network.IsConnected)
            {
                await Network.ConnectAsync(StaticServerUrl);
            }

            if (RegisterRadio.IsChecked == true)
            {
                // === Режим регистрации ===
                string name = NameBox.Text.Trim();

                // Генерируем RSA-ключи, если их ещё нет
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
                        Email = email,
                        Password = password,
                        UserName = string.IsNullOrEmpty(name) ? email : name,
                        PublicKey = PublicKeyPem
                    }
                });

                // Ожидаем ответ с таймаутом 7 сек
                var completed = await Task.WhenAny(_registerTcs.Task, Task.Delay(7000));
                if (completed != _registerTcs.Task)
                {
                    StatusTextBlock.Text = "Таймаут регистрации на сервере.";
                    SubmitButton.IsEnabled = true;
                    return;
                }

                var resp = await _registerTcs.Task;
                if (resp.Status == "Ok")
                {
                    // Успешная регистрация
                    AuthenticatedUserId = resp.AssignedId;
                    AuthenticatedUserName = string.IsNullOrEmpty(name) ? email : name;
                    Token = resp.Token;
                    SaveConfig(email);

                    // Отписываемся перед закрытием, чтобы NetworkService не держал окно
                    UnsubscribeNetworkHandlers();
                    DialogResult = true;
                    Close();
                }
                else
                {
                    // Разные сообщения для разных кодов ошибок сервера
                    StatusTextBlock.Text = resp.Status switch
                    {
                        "UserExists" => "Пользователь с такой почтой уже существует.",
                        "InvalidEmail" => "Некорректный email.",
                        _ => $"Ошибка регистрации: {resp.Status}"
                    };
                    SubmitButton.IsEnabled = true;
                }
            }
            else
            {
                // === Режим входа ===
                _loginTcs = new TaskCompletionSource<LoginResponsePayload>();
                await Network.SendAsync(new
                {
                    Type = "Login",
                    Payload = new LoginPayload
                    {
                        Email = email,
                        Password = password
                    }
                });

                // Ожидаем ответ с таймаутом 7 сек
                var completed = await Task.WhenAny(_loginTcs.Task, Task.Delay(7000));
                if (completed != _loginTcs.Task)
                {
                    StatusTextBlock.Text = "Таймаут авторизации.";
                    SubmitButton.IsEnabled = true;
                    return;
                }

                var resp = await _loginTcs.Task;
                if (resp.Status == "Ok")
                {
                    // Успешный вход
                    AuthenticatedUserId = resp.UserId;
                    AuthenticatedUserName = resp.UserName;
                    Token = resp.Token;

                    // Генерируем ключи, если их ещё нет
                    if (string.IsNullOrEmpty(PublicKeyPem) || string.IsNullOrEmpty(PrivateKeyPem))
                    {
                        var keys = _crypto.GenerateRsaKeyPair();
                        PublicKeyPem = keys.publicKey;
                        PrivateKeyPem = keys.privateKey;
                    }

                    SaveConfig(email);

                    // Отписываемся перед закрытием окна
                    UnsubscribeNetworkHandlers();
                    DialogResult = true;
                    Close();
                }
                else
                {
                    StatusTextBlock.Text = resp.Status == "InvalidCredentials"
                        ? "Неверная почта или пароль."
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