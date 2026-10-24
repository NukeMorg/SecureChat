using System;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SecureChat.Client.Services;
using SecureChat.Common;

namespace SecureChat.Client.View;

/// <summary>
/// Данные сохранённой сессии для автовхода.
/// </summary>
public record AutoLoginSession(int UserId, string UserName, string Token, string PublicKey, string PrivateKey);

/// <summary>
/// Окно входа/регистрации. Регистрация двухшаговая:
/// шаг 1 — Email/ФИО/Пароль, шаг 2 — ввод кода на очищенной форме.
/// Ошибки валидации показываются под полем (inline), серверные/сетевые — во всплывающем окне.
/// </summary>
public partial class LoginWindow : Window
{
    public const string StaticServerUrl = "ws://localhost:9000/";

    public int AuthenticatedUserId { get; private set; }
    public string AuthenticatedUserName { get; private set; } = "User";
    public string Token { get; private set; } = "";
    public string ServerUrl => StaticServerUrl;
    public string PublicKeyPem { get; private set; } = "";
    public string PrivateKeyPem { get; private set; } = "";
    public NetworkService Network { get; private set; }

    private CryptoService _crypto = new();
    private TaskCompletionSource<LoginResponsePayload>? _loginTcs;
    private TaskCompletionSource<RegisterAccountResponsePayload>? _registerTcs;
    private TaskCompletionSource<SendEmailCodeResponsePayload>? _codeTcs;

    private readonly Action<LoginResponsePayload> _loginHandler;
    private readonly Action<RegisterAccountResponsePayload> _registerHandler;
    private readonly Action<SendEmailCodeResponsePayload> _codeHandler;
    private readonly Action<string> _errorHandler;

    private bool _isRegisterMode;
    private bool _isCodeStep;

    private string _pendingEmail = "";
    private string _pendingName = "";
    private string _pendingPassword = "";

    public LoginWindow() : this(new NetworkService()) { }

    public LoginWindow(NetworkService network)
    {
        InitializeComponent();
        Network = network;

        _loginHandler = p => _loginTcs?.TrySetResult(p);
        _registerHandler = p => _registerTcs?.TrySetResult(p);
        _codeHandler = p => _codeTcs?.TrySetResult(p);
        _errorHandler = err => Dispatcher.Invoke(() =>
        {
            // Не показываем диалог, если окно уже закрывается/закрыто —
            // иначе увидим MessageBox после DialogResult = true
            if (!IsLoaded) return;
            ShowErrorPopup($"Ошибка сети: {err}");
        });

        Network.OnLoginResponse += _loginHandler;
        Network.OnRegisterAccountResponse += _registerHandler;
        Network.OnSendEmailCodeResponse += _codeHandler;
        Network.OnError += _errorHandler;

        LoadSavedConfig();

        RegPasswordBox.PasswordChanged += (_, _) => UpdatePasswordStrength();
        UpdateVisualState();
    }

    private void UnsubscribeNetworkHandlers()
    {
        Network.OnLoginResponse -= _loginHandler;
        Network.OnRegisterAccountResponse -= _registerHandler;
        Network.OnSendEmailCodeResponse -= _codeHandler;
        Network.OnError -= _errorHandler;
    }

    // ===== Переключение режима =====

    private void LoginModePanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SetMode(false);
    private void RegisterModePanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SetMode(true);

    private void SetMode(bool isRegister)
    {
        _isRegisterMode = isRegister;
        if (!isRegister) _isCodeStep = false;
        StatusTextBlock.Text = "";
        ClearAllFieldErrors();
        UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        LoginView.Visibility = !_isRegisterMode ? Visibility.Visible : Visibility.Collapsed;
        RegisterFormView.Visibility = (_isRegisterMode && !_isCodeStep) ? Visibility.Visible : Visibility.Collapsed;
        RegisterCodeView.Visibility = (_isRegisterMode && _isCodeStep) ? Visibility.Visible : Visibility.Collapsed;

        // Чекбокс «Запомнить меня» — только на форме входа
        RememberMeCheckBox.Visibility = !_isRegisterMode ? Visibility.Visible : Visibility.Collapsed;

        var active = (Brush)FindResource("InputAreaBackground");
        var fg = (Brush)FindResource("ForegroundColor");
        var muted = (Brush)FindResource("StatusForeground");

        if (_isRegisterMode)
        {
            RegisterModePanel.Background = active;
            RegisterModePanel.BorderThickness = new Thickness(0);
            RegisterModeText.Foreground = fg;

            LoginModePanel.Background = Brushes.Transparent;
            LoginModePanel.BorderBrush = muted;
            LoginModePanel.BorderThickness = new Thickness(1);
            LoginModeText.Foreground = muted;
        }
        else
        {
            LoginModePanel.Background = active;
            LoginModePanel.BorderThickness = new Thickness(0);
            LoginModeText.Foreground = fg;

            RegisterModePanel.Background = Brushes.Transparent;
            RegisterModePanel.BorderBrush = muted;
            RegisterModePanel.BorderThickness = new Thickness(1);
            RegisterModeText.Foreground = muted;
        }

        SubmitButton.Content = !_isRegisterMode
            ? "Войти"
            : _isCodeStep ? "Зарегистрироваться" : "Получить код";
    }

    // ===== Управление ошибками =====

    private static void ShowInlineError(TextBlock target, string message)
    {
        target.Text = message;
        target.Visibility = Visibility.Visible;
    }

    private static void ClearInlineError(TextBlock target)
    {
        target.Text = "";
        target.Visibility = Visibility.Collapsed;
    }

    private void ClearAllFieldErrors()
    {
        ClearInlineError(LoginEmailErrorText);
        ClearInlineError(LoginPasswordErrorText);
        ClearInlineError(RegEmailErrorText);
        ClearInlineError(RegPasswordErrorText);
    }

    /// <summary>
    /// Всплывающее окно для серверных/сетевых ошибок и провалов валидации, не привязанных к полю.
    /// </summary>
    private void ShowErrorPopup(string message)
    {
        MessageBox.Show(message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ===== Валидация =====

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        try
        {
            var addr = new MailAddress(email);
            return addr.Address == email.Trim();
        }
        catch { return false; }
    }

    /// <summary>
    /// Возвращает текст ошибки для поля email или null, если всё в порядке.
    /// В тексте всегда есть пример.
    /// </summary>
    private static string? ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return "Введите email. Пример: example@gmail.com";
        if (!IsValidEmail(email))
            return "Неверный формат почты. Пример: example@gmail.com";
        return null;
    }

    private static string? ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return "Введите пароль.";
        if (password.Length < 8) return "Пароль должен содержать минимум 8 символов.";
        if (!password.Any(char.IsUpper)) return "Пароль должен содержать хотя бы одну заглавную букву.";
        if (!password.Any(char.IsLower)) return "Пароль должен содержать хотя бы одну строчную букву.";
        if (!password.Any(char.IsDigit)) return "Пароль должен содержать хотя бы одну цифру.";
        return null;
    }

    private void UpdatePasswordStrength()
    {
        string pwd = RegPasswordBox.Password;

        if (string.IsNullOrEmpty(pwd))
        {
            PasswordStrengthBar.Value = 0;
            PasswordHintText.Text = "";
            return;
        }

        int score = 0;
        if (pwd.Length >= 8) score++;
        if (pwd.Length >= 12) score++;
        if (pwd.Any(char.IsUpper)) score++;
        if (pwd.Any(char.IsLower)) score++;
        if (pwd.Any(char.IsDigit)) score++;
        if (pwd.Any(c => !char.IsLetterOrDigit(c))) score++;

        double percent = Math.Min(100, score * 100.0 / 6.0);

        Color color;
        string hint;
        if (score <= 2) { color = Color.FromRgb(0xF4, 0x43, 0x36); hint = "Слабый пароль"; }
        else if (score <= 4) { color = Color.FromRgb(0xFF, 0xB3, 0x00); hint = "Средний пароль"; }
        else { color = Color.FromRgb(0x4C, 0xAF, 0x50); hint = "Надёжный пароль"; }

        PasswordStrengthBar.Value = percent;
        PasswordStrengthBar.Foreground = new SolidColorBrush(color);
        PasswordHintText.Text = hint;
        PasswordHintText.Foreground = new SolidColorBrush(color);
    }

    // ===== Основная кнопка =====

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRegisterMode)
        {
            if (!_isCodeStep) await StartRegistrationAsync();
            else await ConfirmRegistrationAsync();
        }
        else
        {
            await LoginAsync();
        }
    }

    // ===== Вход =====

    private async Task LoginAsync()
    {
        ClearAllFieldErrors();

        string email = LoginEmailBox.Text.Trim();
        string password = LoginPasswordBox.Password;

        var emailErr = ValidateEmail(email);
        if (emailErr != null) { ShowInlineError(LoginEmailErrorText, emailErr); return; }

        if (string.IsNullOrEmpty(password))
        {
            ShowInlineError(LoginPasswordErrorText, "Введите пароль.");
            return;
        }

        SubmitButton.IsEnabled = false;
        StatusTextBlock.Foreground = Brushes.Gray;
        StatusTextBlock.Text = "Подключение к серверу...";

        try
        {
            if (!Network.IsConnected)
                await Network.ConnectAsync(StaticServerUrl);

            _loginTcs = new TaskCompletionSource<LoginResponsePayload>();
            await Network.SendAsync(new
            {
                Type = "Login",
                Payload = new LoginPayload { Email = email, Password = password }
            });

            var completed = await Task.WhenAny(_loginTcs.Task, Task.Delay(8000));
            if (completed != _loginTcs.Task)
            {
                StatusTextBlock.Text = "";
                ShowErrorPopup("Таймаут авторизации. Сервер не отвечает.");
                SubmitButton.IsEnabled = true;
                return;
            }

            var resp = await _loginTcs.Task;
            if (resp.Status == "Ok")
            {
                AuthenticatedUserId = resp.UserId;
                AuthenticatedUserName = resp.UserName;
                Token = resp.Token;

                if (string.IsNullOrEmpty(PublicKeyPem) || string.IsNullOrEmpty(PrivateKeyPem))
                {
                    MessageBox.Show(
                        "На этом устройстве не найдены ключи шифрования.\n\n" +
                        "Будет создана новая пара ключей. Это означает, что вы сможете " +
                        "расшифровывать НОВЫЕ сообщения, но НЕ сможете прочитать историю " +
                        "переписки, сохранённую на сервере (она зашифрована под ваш старый ключ).\n\n" +
                        "Чтобы восстановить доступ к старой истории — войдите с того устройства, " +
                        "где ранее выполнялся вход, или восстановите файл config.json.",
                        "Внимание: история недоступна",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    var keys = _crypto.GenerateRsaKeyPair();
                    PublicKeyPem = keys.publicKey;
                    PrivateKeyPem = keys.privateKey;
                }

                    SaveConfig(email, isRegistration: false);
                UnsubscribeNetworkHandlers();
                DialogResult = true;
                Close();
            }
            else
            {
                StatusTextBlock.Text = "";
                ShowErrorPopup(resp.Status == "InvalidCredentials"
                    ? "Неверная почта или пароль."
                    : "Пользователь не найден.");
                SubmitButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "";
            ShowErrorPopup($"Ошибка: {ex.Message}");
            SubmitButton.IsEnabled = true;
        }
    }

    // ===== Регистрация: шаг 1 — форма → отправка кода =====

    private async Task StartRegistrationAsync()
    {
        ClearAllFieldErrors();

        string email = RegEmailBox.Text.Trim();
        string name = RegNameBox.Text.Trim();
        string password = RegPasswordBox.Password;

        var emailErr = ValidateEmail(email);
        if (emailErr != null) { ShowInlineError(RegEmailErrorText, emailErr); return; }

        var pwdErr = ValidatePassword(password);
        if (pwdErr != null) { ShowInlineError(RegPasswordErrorText, pwdErr); return; }

        SubmitButton.IsEnabled = false;
        StatusTextBlock.Foreground = Brushes.Gray;
        StatusTextBlock.Text = "Отправка проверочного кода...";

        try
        {
            if (!Network.IsConnected)
                await Network.ConnectAsync(StaticServerUrl);

            _codeTcs = new TaskCompletionSource<SendEmailCodeResponsePayload>();
            await Network.SendAsync(new
            {
                Type = "SendEmailCode",
                Payload = new SendEmailCodePayload { Email = email, Purpose = "Register" }
            });

            var completed = await Task.WhenAny(_codeTcs.Task, Task.Delay(8000));
            if (completed != _codeTcs.Task)
            {
                StatusTextBlock.Text = "";
                ShowErrorPopup("Таймаут отправки кода. Сервер не отвечает.");
                SubmitButton.IsEnabled = true;
                return;
            }

            var resp = await _codeTcs.Task;
            if (resp.Status != "Ok")
            {
                StatusTextBlock.Text = "";
                ShowErrorPopup(resp.Message);
                SubmitButton.IsEnabled = true;
                return;
            }

            _pendingEmail = email;
            _pendingName = string.IsNullOrWhiteSpace(name) ? email : name;
            _pendingPassword = password;

            RegEmailBox.Clear();
            RegNameBox.Clear();
            RegPasswordBox.Clear();
            UpdatePasswordStrength();

            _isCodeStep = true;
            CodeBox.Text = "";
            CodeInfoText.Text = $"Код подтверждения отправлен на {email}. Проверьте почту или консоль сервера.";
            StatusTextBlock.Text = "";

            UpdateVisualState();
            CodeBox.Focus();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "";
            ShowErrorPopup($"Ошибка: {ex.Message}");
            SubmitButton.IsEnabled = true;
        }
        finally
        {
            SubmitButton.IsEnabled = true;
        }
    }

    // ===== Регистрация: шаг 2 — код → регистрация =====

    private async Task ConfirmRegistrationAsync()
    {
        ClearAllFieldErrors();

        string code = CodeBox.Text.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            ShowErrorPopup("Введите 6-значный код из письма.");
            return;
        }

        SubmitButton.IsEnabled = false;
        StatusTextBlock.Foreground = Brushes.Gray;
        StatusTextBlock.Text = "Регистрация...";

        try
        {
            if (!Network.IsConnected)
                await Network.ConnectAsync(StaticServerUrl);

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
                    Email = _pendingEmail,
                    Code = code,
                    Password = _pendingPassword,
                    UserName = _pendingName,
                    PublicKey = PublicKeyPem
                }
            });

            var completed = await Task.WhenAny(_registerTcs.Task, Task.Delay(8000));
            if (completed != _registerTcs.Task)
            {
                StatusTextBlock.Text = "";
                ShowErrorPopup("Таймаут регистрации. Сервер не отвечает.");
                SubmitButton.IsEnabled = true;
                return;
            }

            var resp = await _registerTcs.Task;
            if (resp.Status == "Ok")
            {
                AuthenticatedUserId = resp.AssignedId;
                AuthenticatedUserName = _pendingName;
                Token = resp.Token;

                SaveConfig(_pendingEmail, isRegistration: true);

                UnsubscribeNetworkHandlers();
                DialogResult = true;
                Close();
            }
            else
            {
                StatusTextBlock.Text = "";
                ShowErrorPopup(resp.Status switch
                {
                    "InvalidCode" => "Неверный код подтверждения.",
                    "CodeExpired" => "Срок действия кода истёк. Запросите новый.",
                    "UserExists" => "Пользователь с такой почтой уже существует.",
                    _ => $"Ошибка регистрации: {resp.Status}"
                });
                SubmitButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "";
            ShowErrorPopup($"Ошибка: {ex.Message}");
            SubmitButton.IsEnabled = true;
        }
    }

    // ===== Кнопки на шаге кода =====

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _isCodeStep = false;
        StatusTextBlock.Text = "";
        UpdateVisualState();
    }

    private async void ResendCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingEmail)) return;

        ResendCodeButton.IsEnabled = false;
        try
        {
            if (!Network.IsConnected)
                await Network.ConnectAsync(StaticServerUrl);

            _codeTcs = new TaskCompletionSource<SendEmailCodeResponsePayload>();
            await Network.SendAsync(new
            {
                Type = "SendEmailCode",
                Payload = new SendEmailCodePayload { Email = _pendingEmail, Purpose = "Register" }
            });

            var completed = await Task.WhenAny(_codeTcs.Task, Task.Delay(8000));
            if (completed == _codeTcs.Task)
            {
                var resp = await _codeTcs.Task;
                if (resp.Status == "Ok")
                {
                    StatusTextBlock.Foreground = Brushes.LimeGreen;
                    StatusTextBlock.Text = "Новый код отправлен.";
                }
                else
                {
                    ShowErrorPopup(resp.Message);
                }
            }
            else
            {
                ShowErrorPopup("Таймаут отправки кода.");
            }
        }
        catch (Exception ex) { ShowErrorPopup($"Ошибка: {ex.Message}"); }
        finally { ResendCodeButton.IsEnabled = true; }
    }

    // ===== Конфиг =====

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecureChat");

    private static string ConfigFile => Path.Combine(ConfigDir, "config.json");

    private void LoadSavedConfig()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;

            var cfg = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(
                File.ReadAllText(ConfigFile));
            if (cfg == null) return;

            if (cfg.TryGetValue("PublicKey", out var pub)) PublicKeyPem = pub;
            if (cfg.TryGetValue("PrivateKey", out var priv)) PrivateKeyPem = priv;

            bool remember = cfg.TryGetValue("RememberMe", out var rem) && rem == "true";
            RememberMeCheckBox.IsChecked = remember;

            if (remember &&
                cfg.TryGetValue("Login", out var log) &&
                !string.IsNullOrWhiteSpace(log))
            {
                LoginEmailBox.Text = log;
            }
        }
        catch { }
    }

    private void SaveConfig(string email, bool isRegistration)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);

            var cfg = new System.Collections.Generic.Dictionary<string, string>
            {
                ["ClientId"] = AuthenticatedUserId.ToString(),
                ["UserName"] = AuthenticatedUserName,
                ["Token"] = Token,
                ["PublicKey"] = PublicKeyPem,
                ["PrivateKey"] = PrivateKeyPem,
                ["Theme"] = App.CurrentTheme
            };

            if (isRegistration)
            {
                cfg["RememberMe"] = "false";
            }
            else
            {
                bool remember = RememberMeCheckBox.IsChecked == true;
                cfg["RememberMe"] = remember ? "true" : "false";
                if (remember)
                    cfg["Login"] = email;
            }

            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(cfg));
        }
        catch { }
    }

    public static void ClearAutoLoginState()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;

            var cfg = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(
                File.ReadAllText(ConfigFile));
            if (cfg == null) return;

            cfg["RememberMe"] = "false";
            cfg.Remove("Login");

            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(cfg));
        }
        catch { }
    }

    public static AutoLoginSession? ReadAutoLoginConfig()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return null;

            var cfg = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(
                File.ReadAllText(ConfigFile));
            if (cfg == null) return null;

            if (!cfg.TryGetValue("RememberMe", out var rem) || rem != "true") return null;
            if (!cfg.TryGetValue("Token", out var token) || string.IsNullOrEmpty(token)) return null;
            if (!cfg.TryGetValue("PublicKey", out var pub) || string.IsNullOrEmpty(pub)) return null;
            if (!cfg.TryGetValue("PrivateKey", out var priv) || string.IsNullOrEmpty(priv)) return null;
            if (!cfg.TryGetValue("ClientId", out var idStr) || !int.TryParse(idStr, out var id)) return null;
            if (!cfg.TryGetValue("UserName", out var name) || string.IsNullOrEmpty(name)) return null;

            return new AutoLoginSession(id, name, token, pub, priv);
        }
        catch { return null; }
    }

    public static AutoLoginSession? TryAutoLoginBlocking(NetworkService network, AutoLoginSession saved, int timeoutMs)
    {
        try
        {
            return Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                try
                {
                    await network.ConnectAsync(StaticServerUrl).WaitAsync(cts.Token);

                    var tcs = new TaskCompletionSource<HelloResponsePayload>();
                    void handler(HelloResponsePayload p) => tcs.TrySetResult(p);
                    network.OnHelloResponse += handler;
                    try
                    {
                        await network.SendAsync(new
                        {
                            Type = "Hello",
                            Payload = new HelloPayload { ClientId = saved.UserId, Token = saved.Token }
                        });

                        var resp = await tcs.Task.WaitAsync(cts.Token);
                        if (resp == null || resp.Status != "Ok") return null;

                        return new AutoLoginSession(
                            resp.UserId,
                            string.IsNullOrEmpty(resp.UserName) ? saved.UserName : resp.UserName,
                            saved.Token,
                            saved.PublicKey,
                            saved.PrivateKey);
                    }
                    finally { network.OnHelloResponse -= handler; }
                }
                catch
                {
                    try { network.Disconnect(); } catch { }
                    return null;
                }
            }).GetAwaiter().GetResult();
        }
        catch
        {
            try { network.Disconnect(); } catch { }
            return null;
        }
    }
}