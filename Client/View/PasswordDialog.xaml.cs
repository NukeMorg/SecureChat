using System.Windows;

namespace SecureChat.Client.View;

/// <summary>
/// Модальный диалог смены пароля. Клиент проверяет совпадение и заполненность;
/// сложность пароля дополнительно проверяет сервер (Validators.IsValidPassword).
/// </summary>
public partial class PasswordDialog : Window
{
    public string CurrentPassword => CurrentPasswordBox.Password;
    public string NewPassword => NewPasswordBox.Password;

    public PasswordDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CurrentPasswordBox.Password))
        {
            MessageBox.Show("Введите текущий пароль.", "Смена пароля", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrEmpty(NewPasswordBox.Password))
        {
            MessageBox.Show("Введите новый пароль.", "Смена пароля", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (NewPasswordBox.Password != ConfirmPasswordBox.Password)
        {
            MessageBox.Show("Новые пароли не совпадают.", "Смена пароля", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
