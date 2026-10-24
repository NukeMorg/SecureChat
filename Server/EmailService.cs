using System;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace SecureChat.Server;

/// <summary>
/// Отправка кодов подтверждения на email.
/// В dev-режиме (по умолчанию) код просто печатается в консоль сервера.
/// </summary>
public class EmailService
{
    // Настройки SMTP-сервера (например, smtp.gmail.com, порт 587)
    private readonly string _smtpHost = "smtp.gmail.com";
    private readonly int _smtpPort = 587;
    private readonly string _senderEmail;     // Почта отправителя
    private readonly string _senderPassword;  // Пароль приложения (App Password)
    private readonly bool _useRealSmtp;

    // Конструктор для получения настроек из DI-контейнера
    public EmailService(IConfiguration configuration)
    {
        _senderEmail = configuration["Smtp:SenderEmail"]!;
        _senderPassword = configuration["Smtp:SenderPassword"]!;
        _useRealSmtp = bool.TryParse(configuration["Smtp:UseRealSmtp"], out var b) ? b : true;
    }

    /// <summary>
    /// Генерирует 6-значный код (криптографически стойкий).
    /// </summary>
    public string GenerateVerificationCode()
    {
        // RandomNumberGenerator предпочтительнее Random для кодов подтверждения
        int num = RandomNumberGenerator.GetInt32(100000, 1000000);
        return num.ToString();
    }

    /// <summary>
    /// Отправляет код на email. Возвращает true, если письмо отправлено (или dev-режим).
    /// </summary>
    public async Task<bool> SendVerificationCodeAsync(string toEmail, string code)
    {
        // Всегда логируем код в консоль — удобно для отладки
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[EMAIL SERVICE] Код подтверждения для {toEmail}: >>> {code} <<<");
        Console.ResetColor();

        // SMTP не настроен — считаем, что код "отправлен" (виден в консоли)
        if (!_useRealSmtp || string.IsNullOrWhiteSpace(_senderEmail) || string.IsNullOrWhiteSpace(_senderPassword))
        {
            return true;
        }

        try
        {
            using var client = new SmtpClient(_smtpHost, _smtpPort)
            {
                Credentials = new NetworkCredential(_senderEmail, _senderPassword),
                EnableSsl = true,
                Timeout = 10000
            };

            var mail = new MailMessage
            {
                From = new MailAddress(_senderEmail, "SecureChat Enterprise"),
                Subject = "Код подтверждения SecureChat",
                Body = $"Здравствуйте!\n\nВаш проверочный код для SecureChat: {code}\nКод действителен 10 минут.",
                IsBodyHtml = false
            };
            mail.To.Add(toEmail);

            await client.SendMailAsync(mail);
            return true;
        }
        catch (Exception ex)
        {
            // Не пробрасываем исключение — вызывающий код получает false
            Console.WriteLine($"[EMAIL SERVICE ERROR] Ошибка отправки письма на {toEmail}: {ex.Message}");
            return false;
        }
    }
}