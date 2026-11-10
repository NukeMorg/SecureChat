using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SecureChat.Client.Services;
using SecureChat.Client.ViewModels;

namespace SecureChat.Client.View;

/// <summary>
/// Представление входа/регистрации: держит только визуальное состояние
/// (переключение панелей, подсветка режимов, фокус) и синхронизацию
/// PasswordBox/кода с ViewModel — вся логика в <see cref="LoginViewModel"/>.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;

    // Результат аутентификации для App/MainWindow (после DialogResult=true)
    public int AuthenticatedUserId => _vm.AuthenticatedUserId;
    public string AuthenticatedUserName => _vm.AuthenticatedUserName;
    public string AuthenticatedUserRole => _vm.AuthenticatedUserRole;
    public string Token => _vm.Token;
    public string ServerUrl => ConfigService.DefaultServerUrl;
    public string PublicKeyPem => _vm.PublicKeyPem;
    public string PrivateKeyPem => _vm.PrivateKeyPem;
    public NetworkService Network { get; private set; }

    public LoginWindow() : this(new NetworkService()) { }

    public LoginWindow(NetworkService network)
    {
        InitializeComponent();
        Network = network;

        _vm = new LoginViewModel(network, new DialogService());
        DataContext = _vm;

        var saved = ConfigService.ReadSavedCredentials();
        if (saved != null) _vm.LoadSavedCredentials(saved);

        _vm.CloseRequested += (_, _) =>
        {
            DialogResult = true;
            Close();
        };

        // видимость панелей и подсветка режимов — реакция на состояние ViewModel
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LoginViewModel.IsRegisterMode) or nameof(LoginViewModel.IsCodeStep))
                UpdateVisualState();
            if (e.PropertyName == nameof(LoginViewModel.IsCodeStep) && _vm.IsCodeStep)
            {
                RegEmailBox.Clear();
                RegNameBox.Clear();
                RegPasswordBox.Clear();
                CodeBox.Clear();
                CodeBox.Focus();
            }
        };

        // PasswordBox не поддерживает прямую привязку — синхронизируем с ViewModel вручную
        LoginPasswordBox.PasswordChanged += (_, _) => _vm.LoginPassword = LoginPasswordBox.Password;
        RegPasswordBox.PasswordChanged += (_, _) =>
        {
            _vm.RegPassword = RegPasswordBox.Password;
            _vm.UpdatePasswordStrength();
        };
        CodeBox.TextChanged += (_, _) => _vm.Code = CodeBox.Text;

        Closed += (_, _) => _vm.Detach();

        UpdateVisualState();
    }

    private void LoginModePanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _vm.SetLoginModeCommand.Execute(null);

    private void RegisterModePanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _vm.SetRegisterModeCommand.Execute(null);

    private void UpdateVisualState()
    {
        LoginView.Visibility = !_vm.IsRegisterMode ? Visibility.Visible : Visibility.Collapsed;
        RegisterFormView.Visibility = (_vm.IsRegisterMode && !_vm.IsCodeStep) ? Visibility.Visible : Visibility.Collapsed;
        RegisterCodeView.Visibility = (_vm.IsRegisterMode && _vm.IsCodeStep) ? Visibility.Visible : Visibility.Collapsed;
        RememberMeCheckBox.Visibility = !_vm.IsRegisterMode ? Visibility.Visible : Visibility.Collapsed;

        var active = (Brush)FindResource("InputAreaBackground");
        var fg = (Brush)FindResource("ForegroundColor");
        var muted = (Brush)FindResource("StatusForeground");

        if (_vm.IsRegisterMode)
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
    }
}
