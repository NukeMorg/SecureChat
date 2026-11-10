namespace SecureChat.Server.Auth;

/// <summary>Настройки SMTP (секция "Smtp") для отправки писем с кодами подтверждения.</summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public string SenderEmail { get; set; } = "";
    public string SenderPassword { get; set; } = "";
    public bool UseRealSmtp { get; set; }
}
