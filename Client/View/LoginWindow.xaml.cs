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
/// Окно входа/регистрации: подключение к серверу, авторизация по email,
/// запрос кода подтверждения и регистрация с проверкой кода.
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
    // TCS для ожидания ответов сервера
    private TaskCompletionSource<LoginResponsePayload>? _loginTcs;
    private TaskCompletionSource<RegisterAccountResponsePayload>? _registerTcs;
    private TaskCompletionSource<SendEmailCodeResponsePayload>? _codeTcs;

    // Ссылки на делегаты — нужны, чтобы отписаться от событий перед закрытием окна
    private readonly Action<LoginResponsePayload> _loginHandler;
    private readonly Action<RegisterAccountResponsePayload> _registerHandler;
    private readonly Action<SendEmailCodeResponsePayload> _codeHandler;
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
        _codeHandler = p => _codeTcs?.TrySetResult(p);
        _errorHandler = err => Dispatcher.Invoke(() => StatusTextBlock.Text = $"Ошибка: {err}");

        Network.OnLoginResponse += _loginHandler;
        Network.OnRegisterAccountResponse += _registerHandler;
        Network.OnSendEmailCodeResponse += _codeHandler;
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
        Network.OnSendEmailCodeResponse -= _codeHandler;
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
    /// Переключение режимов "Вход" / "Регистрация" (показ панели имени и поля кода).
    /// </summary>
    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (NamePanel == null || CodePanel == null || SubmitButton == null) return;
        bool isRegister = RegisterRadio.IsChecked == true;
        NamePanel.Visibility = isRegister ? Visibility.Visible : Visibility.Collapsed;
        CodePanel.Visibility = isRegister ? Visibility.Visible : Visibility.Collapsed;
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
    /// Запрос кода подтверждения на указанный email (режим регистрации).
    /// </summary>
    private async void GetCodeButton_Click(object sender, RoutedEventArgs e)
    {
        string email = LoginBox.Text.Trim();
        if (string.IsNullOrEmpty(email) || !IsValidEmail(email))
        {
            StatusTextBlock.Text = "Сначала введите корректный Email.";
            return;
        }

        GetCodeButton.IsEnabled = false;
        StatusTextBlock.Foreground = System.Windows.Media.Brushes.Gray;
        StatusTextBlock.Text = "Отправка проверочного кода...";

        try
        {
            // Подключаемся, если ещё нет соединения
            if (!Network.IsConnected)
            {
                await Network.ConnectAsync(StaticServerUrl);
            }

            _codeTcs = new TaskCompletionSource<SendEmailCodeResponsePayload>();
            await Network.SendAsync(new
            {
                Type = "SendEmailCode",
                Payload = new SendEmailCodePayload
                {
                    Email = email,
                    Purpose = "Register"
                }
            });

            // Ожидаем ответ с таймаутом 8 сек
            var completed = await Task.WhenAny(_codeTcs.Task, Task.Delay(8000));
            if (completed != _codeTcs.Task)
            {
                StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                StatusTextBlock.Text = "Таймаут отправки кода.";
                GetCodeButton.IsEnabled = true;
                return;
            }

            var resp = await _codeTcs.Task;
            if (resp.Status == "Ok")
            {
                StatusTextBlock.Foreground = System.Windows.Media.Brushes.LimeGreen;
                StatusTextBlock.Text = "Код подтверждения отправлен (проверьте почту или консоль сервера)!";
            }
            else
            {
                // Сервер вернул готовое сообщение об ошибке
                StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                StatusTextBlock.Text = resp.Message;
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            StatusTextBlock.Text = $"Ошибка: {ex.Message}";
        }
        finally
        {
            GetCodeButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Основная кнопка: вход или регистрация (с обязательной проверкой кода).
    /// </summary>
    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        string email = LoginBox.Text.Trim();
        string password = PasswordBox.Password;

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            StatusTextBlock.Text = "Заполните почту и пароль.";
            return;
        }

        // Локальная проверка email до обращения к серверу
        if (!IsValidEmail(email))
        {
            StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            StatusTextBlock.Text = "Введите корректный Email.";
            return;
        }

        SubmitButton.IsEnabled = false;
        StatusTextBlock.Foreground = System.Windows.Media.Brushes.Gray;
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
                string code = CodeBox.Text.Trim();
                if (string.IsNullOrEmpty(code) || code.Length != 6)
                {
                    StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                    StatusTextBlock.Text = "Введите 6-значный проверочный код из письма.";
                    SubmitButton.IsEnabled = true;
                    return;
                }

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
                        Code = code,
                        Password = password,
                        UserName = string.IsNullOrEmpty(name) ? email : name,
                        PublicKey = PublicKeyPem
                    }
                });

                // Ожидаем ответ с таймаутом 8 сек
                var completed = await Task.WhenAny(_registerTcs.Task, Task.Delay(8000));
                if (completed != _registerTcs.Task)
                {
                    StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
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
                    // Разбор кодов ошибок сервера (в т.ч. новых — InvalidCode/CodeExpired)
                    StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                    StatusTextBlock.Text = resp.Status switch
                    {
                        "InvalidCode" => "Неверный код подтверждения.",
                        "CodeExpired" => "Срок действия кода истек. Запросите новый.",
                        "UserExists" => "Пользователь с такой почтой уже существует.",
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

                // Ожидаем ответ с таймаутом 8 сек
                var completed = await Task.WhenAny(_loginTcs.Task, Task.Delay(8000));
                if (completed != _loginTcs.Task)
                {
                    StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
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
                    StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                    StatusTextBlock.Text = resp.Status == "InvalidCredentials"
                        ? "Неверная почта или пароль."
                        : "Пользователь не найден.";
                    SubmitButton.IsEnabled = true;
                }
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            StatusTextBlock.Text = $"Ошибка: {ex.Message}";
            SubmitButton.IsEnabled = true;
        }
    }
}