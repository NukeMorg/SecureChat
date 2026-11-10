using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SecureChat.Client.Services;
using SecureChat.Core;
using Serilog;

namespace SecureChat.Client.ViewModels;

/// <summary>
/// ViewModel окна входа/регистрации: валидация полей, 3 шага регистрации
/// (учётные данные → код из письма → подтверждение), вход по email/паролю,
/// индикатор надёжности пароля. Результат аутентификации читает App после
/// закрытия окна через свойства <see cref="AuthenticatedUserId"/> и т.д.
/// </summary>
public partial class LoginViewModel : ObservableObject
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LoginViewModel>();

    private readonly NetworkService _network;
    private readonly CryptoService _crypto = new();
    private readonly IDialogService _dialogs;

    // ----- Результат аутентификации (наружу для App/MainWindow) -----
    public int AuthenticatedUserId { get; private set; }
    public string AuthenticatedUserName { get; private set; } = "User";
    public string AuthenticatedUserRole { get; private set; } = "Employee";
    public string Token { get; private set; } = "";
    public string PublicKeyPem { get; private set; } = "";
    public string PrivateKeyPem { get; private set; } = "";

    // ----- Поля ввода -----
    [ObservableProperty] private string _loginEmail = "";
    [ObservableProperty] private string _loginPassword = "";
    [ObservableProperty] private string _regEmail = "";
    [ObservableProperty] private string _regName = "";
    [ObservableProperty] private string _regPassword = "";
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private bool _rememberMe;

    // ----- Состояние формы -----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubmitButtonText))]
    private bool _isRegisterMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubmitButtonText))]
    private bool _isCodeStep;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isBusy;

    // ----- Ошибки полей (inline) -----
    [ObservableProperty] private string _loginEmailError = "";
    [ObservableProperty] private string _loginPasswordError = "";
    [ObservableProperty] private string _regEmailError = "";
    [ObservableProperty] private string _regPasswordError = "";

    // ----- Индикатор надёжности пароля -----
    [ObservableProperty] private double _strengthPercent;
    [ObservableProperty] private Brush _strengthBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
    [ObservableProperty] private string _strengthHint = "";

    [ObservableProperty] private string _codeInfoText = "";

    /// <summary>Текст кнопки подтверждения в зависимости от режима.</summary>
    public string SubmitButtonText => !IsRegisterMode ? "Войти" : IsCodeStep ? "Зарегистрироваться" : "Получить код";

    /// <summary>Кнопка подтверждения доступна, когда не идёт сетевая операция.</summary>
    public bool CanSubmit => !IsBusy;

    /// <summary>Представление закрывает окно с DialogResult=true.</summary>
    public event EventHandler? CloseRequested;

    private string _pendingEmail = "";
    private string _pendingName = "";
    private string _pendingPassword = "";

    private TaskCompletionSource<LoginResponsePayload>? _loginTcs;
    private TaskCompletionSource<RegisterAccountResponsePayload>? _registerTcs;
    private TaskCompletionSource<SendEmailCodeResponsePayload>? _codeTcs;

    private readonly Action<LoginResponsePayload> _loginHandler;
    private readonly Action<RegisterAccountResponsePayload> _registerHandler;
    private readonly Action<SendEmailCodeResponsePayload> _codeHandler;
    private readonly Action<string> _errorHandler;

    public LoginViewModel(NetworkService network, IDialogService dialogs)
    {
        _network = network;
        _dialogs = dialogs;

        _loginHandler = p => _loginTcs?.TrySetResult(p);
        _registerHandler = p => _registerTcs?.TrySetResult(p);
        _codeHandler = p => _codeTcs?.TrySetResult(p);
        _errorHandler = err => _dialogs.ShowError($"Ошибка сети: {err}");

        _network.OnLoginResponse += _loginHandler;
        _network.OnRegisterAccountResponse += _registerHandler;
        _network.OnSendEmailCodeResponse += _codeHandler;
        _network.OnError += _errorHandler;
    }

    /// <summary>Отписка от событий сети (при закрытии окна).</summary>
    public void Detach()
    {
        _network.OnLoginResponse -= _loginHandler;
        _network.OnRegisterAccountResponse -= _registerHandler;
        _network.OnSendEmailCodeResponse -= _codeHandler;
        _network.OnError -= _errorHandler;
    }

    /// <summary>Предзаполнение из config.json: ключи шифрования и «Запомнить меня».</summary>
    public void LoadSavedCredentials(SavedCredentials saved)
    {
        PublicKeyPem = saved.PublicKeyPem;
        PrivateKeyPem = saved.PrivateKeyPem;
        RememberMe = saved.RememberMe;
        if (saved.RememberMe && !string.IsNullOrWhiteSpace(saved.Login))
            LoginEmail = saved.Login;
    }

    private void SetMode(bool isRegister)
    {
        IsRegisterMode = isRegister;
        if (!isRegister) IsCodeStep = false;
        StatusText = "";
        ClearAllFieldErrors();
    }

    [RelayCommand]
    private void SetLoginMode() => SetMode(false);

    [RelayCommand]
    private void SetRegisterMode() => SetMode(true);

    [RelayCommand]
    private void Back()
    {
        IsCodeStep = false;
        StatusText = "";
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsRegisterMode)
        {
            if (!IsCodeStep) await StartRegistrationAsync();
            else await ConfirmRegistrationAsync();
        }
        else
        {
            await LoginAsync();
        }
    }

    [RelayCommand]
    private async Task ResendCodeAsync()
    {
        if (string.IsNullOrEmpty(_pendingEmail)) return;

        try
        {
            if (!_network.IsConnected)
                await _network.ConnectAsync(ConfigService.DefaultServerUrl);

            _codeTcs = new TaskCompletionSource<SendEmailCodeResponsePayload>();
            await _network.SendEmailCodeAsync(new SendEmailCodePayload { Email = _pendingEmail, Purpose = "Register" });

            var completed = await Task.WhenAny(_codeTcs.Task, Task.Delay(8000));
            if (completed == _codeTcs.Task)
            {
                var resp = await _codeTcs.Task;
                if (resp.Status == "Ok")
                {
                    Log.Information("Code resent");
                    StatusBrush = Brushes.LimeGreen;
                    StatusText = "Новый код отправлен.";
                }
                else
                {
                    Log.Warning("ResendCode rejected status={Status}", resp.Status);
                    _dialogs.ShowError(resp.Message);
                }
            }
            else
            {
                _dialogs.ShowError("Code send timeout.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in ResendCode");
            _dialogs.ShowError($"Ошибка: {ex.Message}");
        }
    }

    private async Task LoginAsync()
    {
        ClearAllFieldErrors();

        string email = LoginEmail.Trim();
        string password = LoginPassword;

        var emailErr = ValidateEmail(email);
        if (emailErr != null) { LoginEmailError = emailErr; return; }
        if (string.IsNullOrEmpty(password))
        {
            LoginPasswordError = "Введите пароль.";
            return;
        }

        IsBusy = true;
        StatusBrush = Brushes.Gray;
        StatusText = "Подключение к серверу...";

        try
        {
            if (!_network.IsConnected)
                await _network.ConnectAsync(ConfigService.DefaultServerUrl);

            _loginTcs = new TaskCompletionSource<LoginResponsePayload>();
            await _network.LoginAsync(new LoginPayload { Email = email, Password = password });

            var completed = await Task.WhenAny(_loginTcs.Task, Task.Delay(8000));
            if (completed != _loginTcs.Task)
            {
                Log.Warning("Login timeout");
                StatusText = "";
                _dialogs.ShowError("Login timeout. Сервер не отвечает.");
                return;
            }

            var resp = await _loginTcs.Task;
            if (resp.Status == "Ok")
            {
                AuthenticatedUserId = resp.UserId;
                AuthenticatedUserName = resp.UserName;
                AuthenticatedUserRole = string.IsNullOrEmpty(resp.Role) ? "Employee" : resp.Role;
                Token = resp.Token;

                if (string.IsNullOrEmpty(PublicKeyPem) || string.IsNullOrEmpty(PrivateKeyPem))
                {
                    _dialogs.ShowWarning(
                        "На этом устройстве не найдены ключи шифрования.\n\n" +
                        "Будет создана новая пара ключей. Это означает, что вы сможете " +
                        "расшифровывать НОВЫЕ сообщения, но НЕ сможете прочитать историю " +
                        "переписки, сохранённую на сервере.\n\n" +
                        "Чтобы восстановить доступ к старой истории — войдите с того устройства, " +
                        "где ранее выполнялся вход, или восстановите файл config.json.",
                        "Внимание: история недоступна");

                    var keys = _crypto.GenerateRsaKeyPair();
                    PublicKeyPem = keys.publicKey;
                    PrivateKeyPem = keys.privateKey;
                    Log.Warning("New keys generated for userId={UserId} (history unavailable)", resp.UserId);
                }

                Log.Information("Login successful userId={UserId}", resp.UserId);
                Log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} success=true", "Login", resp.UserId);

                ConfigService.SaveSession(
                    new AutoLoginSession(AuthenticatedUserId, AuthenticatedUserName, Token,
                        PublicKeyPem, PrivateKeyPem, AuthenticatedUserRole),
                    rememberMe: RememberMe, login: email);

                Detach();
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Log.Warning("Login rejected status={Status}", resp.Status);
                StatusText = "";
                _dialogs.ShowError(resp.Status switch
                {
                    "InvalidCredentials" => "Неверная почта или пароль.",
                    "RateLimited" => "Слишком много попыток входа. Попробуйте через 15 минут.",
                    "Banned" => "Аккаунт заблокирован администратором.",
                    _ => "Пользователь не найден."
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in LoginAsync");
            StatusText = "";
            _dialogs.ShowError($"Ошибка: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartRegistrationAsync()
    {
        ClearAllFieldErrors();

        string email = RegEmail.Trim();
        string name = RegName.Trim();
        string password = RegPassword;

        var emailErr = ValidateEmail(email);
        if (emailErr != null) { RegEmailError = emailErr; return; }

        var pwdErr = ValidatePassword(password);
        if (pwdErr != null) { RegPasswordError = pwdErr; return; }

        IsBusy = true;
        StatusBrush = Brushes.Gray;
        StatusText = "Отправка проверочного кода...";

        try
        {
            if (!_network.IsConnected)
                await _network.ConnectAsync(ConfigService.DefaultServerUrl);

            _codeTcs = new TaskCompletionSource<SendEmailCodeResponsePayload>();
            await _network.SendEmailCodeAsync(new SendEmailCodePayload { Email = email, Purpose = "Register" });

            var completed = await Task.WhenAny(_codeTcs.Task, Task.Delay(8000));
            if (completed != _codeTcs.Task)
            {
                Log.Warning("Code send timeout");
                StatusText = "";
                _dialogs.ShowError("Code send timeout. Сервер не отвечает.");
                return;
            }

            var resp = await _codeTcs.Task;
            if (resp.Status != "Ok")
            {
                Log.Warning("SendEmailCode rejected status={Status}", resp.Status);
                StatusText = "";
                _dialogs.ShowError(resp.Message);
                return;
            }

            _pendingEmail = email;
            _pendingName = string.IsNullOrWhiteSpace(name) ? email : name;
            _pendingPassword = password;

            IsCodeStep = true;
            CodeInfoText = $"Код подтверждения отправлен на {email}. Проверьте почту или консоль сервера.";
            StatusText = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in StartRegistrationAsync");
            StatusText = "";
            _dialogs.ShowError($"Ошибка: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConfirmRegistrationAsync()
    {
        ClearAllFieldErrors();

        string code = Code.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            _dialogs.ShowError("Введите 6-значный код из письма.");
            return;
        }

        IsBusy = true;
        StatusBrush = Brushes.Gray;
        StatusText = "Регистрация...";

        try
        {
            if (!_network.IsConnected)
                await _network.ConnectAsync(ConfigService.DefaultServerUrl);

            if (string.IsNullOrEmpty(PublicKeyPem) || string.IsNullOrEmpty(PrivateKeyPem))
            {
                var keys = _crypto.GenerateRsaKeyPair();
                PublicKeyPem = keys.publicKey;
                PrivateKeyPem = keys.privateKey;
            }

            _registerTcs = new TaskCompletionSource<RegisterAccountResponsePayload>();
            await _network.RegisterAccountAsync(new RegisterAccountPayload
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
                StatusText = "";
                _dialogs.ShowError("Registration timeout. Сервер не отвечает.");
                return;
            }

            var resp = await _registerTcs.Task;
            if (resp.Status == "Ok")
            {
                AuthenticatedUserId = resp.AssignedId;
                AuthenticatedUserName = _pendingName;
                AuthenticatedUserRole = string.IsNullOrEmpty(resp.Role) ? "Employee" : resp.Role;
                Token = resp.Token;

                Log.Information("Registration successful userId={UserId}", resp.AssignedId);
                Log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} success=true", "Register", resp.AssignedId);

                ConfigService.SaveSession(
                    new AutoLoginSession(AuthenticatedUserId, AuthenticatedUserName, Token,
                        PublicKeyPem, PrivateKeyPem, AuthenticatedUserRole),
                    rememberMe: false, login: _pendingEmail);

                Detach();
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Log.Warning("Registration rejected status={Status}", resp.Status);
                StatusText = "";
                _dialogs.ShowError(resp.Status switch
                {
                    "InvalidCode" => "Неверный код подтверждения.",
                    "CodeExpired" => "Срок действия кода истёк. Запросите новый.",
                    "UserExists" => "Пользователь с такой почтой уже существует.",
                    "InvalidEmail" => "Неверный формат почты. Пример: example@gmail.com",
                    "WeakPassword" => "Пароль: минимум 8 символов, хотя бы одна буква и одна цифра.",
                    "InvalidUserName" => "Некорректное имя пользователя.",
                    _ => $"Ошибка регистрации: {resp.Status}"
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in ConfirmRegistrationAsync");
            StatusText = "";
            _dialogs.ShowError($"Ошибка: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ----- Валидация и индикатор надёжности пароля -----

    private static bool IsValidEmail(string email) =>
        Validators.IsValidEmail(email);

    private static string? ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "Введите email. Пример: example@gmail.com";
        if (!IsValidEmail(email)) return "Неверный формат почты. Пример: example@gmail.com";
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

    /// <summary>Пересчёт индикатора надёжности пароля (вызывает представление при изменении пароля).</summary>
    public void UpdatePasswordStrength()
    {
        string pwd = RegPassword;
        if (string.IsNullOrEmpty(pwd))
        {
            StrengthPercent = 0;
            StrengthHint = "";
            return;
        }

        int score = 0;
        if (pwd.Length >= 8) score++;
        if (pwd.Length >= 12) score++;
        if (pwd.Any(char.IsUpper)) score++;
        if (pwd.Any(char.IsLower)) score++;
        if (pwd.Any(char.IsDigit)) score++;
        if (pwd.Any(c => !char.IsLetterOrDigit(c))) score++;

        StrengthPercent = Math.Min(100, score * 100.0 / 6.0);
        if (score <= 2)
        {
            StrengthBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
            StrengthHint = "Слабый пароль";
        }
        else if (score <= 4)
        {
            StrengthBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00));
            StrengthHint = "Средний пароль";
        }
        else
        {
            StrengthBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
            StrengthHint = "Надёжный пароль";
        }
    }

    private void ClearAllFieldErrors()
    {
        LoginEmailError = "";
        LoginPasswordError = "";
        RegEmailError = "";
        RegPasswordError = "";
    }
}
