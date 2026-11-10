using System.Threading.Tasks;

namespace SecureChat.Server;

/// <summary>Генерация и отправка кодов подтверждения регистрации (2FA-credential).</summary>
public interface IEmailService
{
    /// <summary>6-значный код из CSPRNG.</summary>
    string GenerateVerificationCode();

    /// <summary>Отправка письма с кодом; без настроенного SMTP — dev-режим (код в лог).</summary>
    Task<bool> SendVerificationCodeAsync(string toEmail, string code);
}
