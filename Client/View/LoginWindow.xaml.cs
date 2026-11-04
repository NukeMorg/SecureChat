using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SecureChat.Client.Services;
using SecureChat.Core;
using Serilog;

namespace SecureChat.Client.View;

public record AutoLoginSession(int UserId, string UserName, string Token, string PublicKey, string PrivateKey);

public partial class LoginWindow : Window
{
    public const string StaticServerUrl = "ws://localhost:9000/";

    private static readonly ILogger Log = Serilog.Log.ForContext<LoginWindow>();

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

    private void ShowErrorPopup(string message) =>
        MessageBox.Show(message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);

    private static bool IsValidEmail(string email) =>
        Validators.IsValidEmail(email);

    private static string? ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "Введите email. Пример: example@company.com";
        if (!IsValidEmail(email)) return "Допустима только корпоративная почта. Пример: example@company.com";
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
            await Network.LoginAsync(new LoginPayload { Email = email, Password = password });

            var completed = await Task.WhenAny(_loginTcs.Task, Task.Delay(8000));
            if (completed != _loginTcs.Task)
            {
                Log.Warning("Login timeout");
                StatusTextBlock.Text = "";
                ShowErrorPopup("Login timeout. Сервер не отвечает.");
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
                        "переписки, сохранённую на сервере.\n\n" +
                        "Чтобы восстановить доступ к старой истории — войдите с того устройства, " +
                        "где ранее выполнялся вход, или восстановите файл config.json.",
                        "Внимание: история недоступна",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    var keys = _crypto.GenerateRsaKeyPair();
                    PublicKeyPem = keys.publicKey;
                    PrivateKeyPem = keys.privateKey;
                    Log.Warning("New keys generated for userId={UserId} (history unavailable)", resp.UserId);
                }

                Log.Information("Login successful userId={UserId}", resp.UserId);
                Log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} success=true", "Login", resp.UserId);

                SaveConfig(email, isRegistration: false);
                UnsubscribeNetworkHandlers();
                DialogResult = true;
                Close();
            }
            else
            {
                Log.Warning("Login rejected status={Status}", resp.Status);
                StatusTextBlock.Text = "";
                ShowErrorPopup(resp.Status switch
                {
                    "InvalidCredentials" => "Неверная почта или пароль.",
                    "RateLimited" => "Слишком много попыток входа. Попробуйте через 15 минут.",
                    _ => "Пользователь не найден."
                });
                SubmitButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in LoginAsync");
            StatusTextBlock.Text = "";
            ShowErrorPopup($"Ошибка: {ex.Message}");
            SubmitButton.IsEnabled = true;
        }
    }

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
            await Network.SendEmailCodeAsync(new SendEmailCodePayload { Email = email, Purpose = "Register" });

            var completed = await Task.WhenAny(_codeTcs.Task, Task.Delay(8000));
            if (completed != _codeTcs.Task)
            {
                Log.Warning("Code send timeout");
                StatusTextBlock.Text = "";
                ShowErrorPopup("Code send timeout. Сервер не отвечает.");
                SubmitButton.IsEnabled = true;
                return;
            }

            var resp = await _codeTcs.Task;
            if (resp.Status != "Ok")
            {
                Log.Warning("SendEmailCode rejected status={Status}", resp.Status);
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
            Log.Error(ex, "Error in StartRegistrationAsync");
            StatusTextBlock.Text = "";
            ShowErrorPopup($"Ошибка: {ex.Message}");
            SubmitButton.IsEnabled = true;
        }
        finally
        {
            SubmitButton.IsEnabled = true;
        }
    }

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
            await Network.RegisterAccountAsync(new RegisterAccountPayload
            {
                Email = _pendingEmail,
                Code = code,
                Password = _pendingPassword,
                UserName = _pendingName,
                PublicKey = PublicKeyPem
            });

            var completed = await Task.WhenAny(_registerTcs.Task, Task.Delay(8000));
            if (completed != _registerTcs.Task)
            {
                Log.Warning("Registration timeout");
                StatusTextBlock.Text = "";
                ShowErrorPopup("Registration timeout. Сервер не отвечает.");
                SubmitButton.IsEnabled = true;
                return;
            }

            var resp = await _registerTcs.Task;
            if (resp.Status == "Ok")
            {
                AuthenticatedUserId = resp.AssignedId;
                AuthenticatedUserName = _pendingName;
                Token = resp.Token;

                Log.Information("Registration successful userId={UserId}", resp.AssignedId);
                Log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} success=true", "Register", resp.AssignedId);

                SaveConfig(_pendingEmail, isRegistration: true);
                UnsubscribeNetworkHandlers();
                DialogResult = true;
                Close();
            }
            else
            {
                Log.Warning("Registration rejected status={Status}", resp.Status);
                StatusTextBlock.Text = "";
                ShowErrorPopup(resp.Status switch
                {
                    "InvalidCode" => "Неверный код подтверждения.",
                    "CodeExpired" => "Срок действия кода истёк. Запросите новый.",
                    "UserExists" => "Пользователь с такой почтой уже существует.",
                    "InvalidEmail" => "Допустима только корпоративная почта @company.com.",
                    "WeakPassword" => "Пароль: минимум 8 символов, хотя бы одна буква и одна цифра.",
                    "InvalidUserName" => "Некорректное имя пользователя.",
                    _ => $"Ошибка регистрации: {resp.Status}"
                });
                SubmitButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in ConfirmRegistrationAsync");
            StatusTextBlock.Text = "";
            ShowErrorPopup($"Ошибка: {ex.Message}");
            SubmitButton.IsEnabled = true;
        }
    }

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
            await Network.SendEmailCodeAsync(new SendEmailCodePayload { Email = _pendingEmail, Purpose = "Register" });

            var completed = await Task.WhenAny(_codeTcs.Task, Task.Delay(8000));
            if (completed == _codeTcs.Task)
            {
                var resp = await _codeTcs.Task;
                if (resp.Status == "Ok")
                {
                    Log.Information("Code resent");
                    StatusTextBlock.Foreground = Brushes.LimeGreen;
                    StatusTextBlock.Text = "Новый код отправлен.";
                }
                else
                {
                    Log.Warning("ResendCode rejected status={Status}", resp.Status);
                    ShowErrorPopup(resp.Message);
                }
            }
            else
            {
                ShowErrorPopup("Code send timeout.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in ResendCodeButton_Click");
            ShowErrorPopup($"Ошибка: {ex.Message}");
        }
        finally { ResendCodeButton.IsEnabled = true; }
    }

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecureChat");

    private static string ConfigFile => Path.Combine(ConfigDir, "config.json");

    private void LoadSavedConfig()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;
            var cfg = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(ConfigFile));
            if (cfg == null) return;

            if (cfg.TryGetValue("PublicKey", out var pub)) PublicKeyPem = pub;
            if (cfg.TryGetValue("PrivateKey", out var priv))
            {
                string? decrypted = SecretProtector.Unprotect(priv);
                if (decrypted != null) PrivateKeyPem = decrypted;
            }
            bool remember = cfg.TryGetValue("RememberMe", out var rem) && rem == "true";
            RememberMeCheckBox.IsChecked = remember;
            if (remember && cfg.TryGetValue("Login", out var log) && !string.IsNullOrWhiteSpace(log))
                LoginEmailBox.Text = log;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read config.json");
        }
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
                // секреты шифруются через DPAPI (CurrentUser)
                ["Token"] = SecretProtector.Protect(Token),
                ["PublicKey"] = PublicKeyPem,
                ["PrivateKey"] = SecretProtector.Protect(PrivateKeyPem),
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
                if (remember) cfg["Login"] = email;
            }

            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(cfg));
            Log.Debug("config.json saved");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save config.json");
        }
    }

    public static void ClearAutoLoginState()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;
            var cfg = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(ConfigFile));
            if (cfg == null) return;
            cfg["RememberMe"] = "false";
            cfg.Remove("Login");
            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(cfg));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clear auto-login");
        }
    }

    public static AutoLoginSession? ReadAutoLoginConfig()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return null;
            var cfg = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(ConfigFile));
            if (cfg == null) return null;
            if (!cfg.TryGetValue("RememberMe", out var rem) || rem != "true") return null;
            if (!cfg.TryGetValue("Token", out var tokenRaw) || string.IsNullOrEmpty(tokenRaw)) return null;
            if (!cfg.TryGetValue("PublicKey", out var pub) || string.IsNullOrEmpty(pub)) return null;
            if (!cfg.TryGetValue("PrivateKey", out var privRaw) || string.IsNullOrEmpty(privRaw)) return null;
            if (!cfg.TryGetValue("ClientId", out var idStr) || !int.TryParse(idStr, out var id)) return null;
            if (!cfg.TryGetValue("UserName", out var name) || string.IsNullOrEmpty(name)) return null;

            string? token = SecretProtector.Unprotect(tokenRaw);
            string? privateKey = SecretProtector.Unprotect(privRaw);
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(privateKey)) return null;

            return new AutoLoginSession(id, name, token, pub, privateKey);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read auto-login");
            return null;
        }
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
                        await network.HelloAsync(new HelloPayload { ClientId = saved.UserId, Token = saved.Token });

                        var resp = await tcs.Task.WaitAsync(cts.Token);
                        if (resp == null || resp.Status != "Ok") return null;

                        return new AutoLoginSession(
                            resp.UserId,
                            string.IsNullOrEmpty(resp.UserName) ? saved.UserName : resp.UserName,
                            saved.Token, saved.PublicKey, saved.PrivateKey);
                    }
                    finally { network.OnHelloResponse -= handler; }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "TryAutoLoginBlocking failed");
                    try { network.Disconnect(); } catch { }
                    return null;
                }
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TryAutoLoginBlocking outer error");
            try { network.Disconnect(); } catch { }
            return null;
        }
    }
}