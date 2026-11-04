using System;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace SecureChat.Server;

public class EmailService
{
    private readonly string _smtpHost = "smtp.gmail.com";
    private readonly int _smtpPort = 587;
    private readonly string _senderEmail;
    private readonly string _senderPassword;
    private readonly bool _useRealSmtp;

    private static readonly ILogger Log = Serilog.Log.ForContext<EmailService>();

    public EmailService(IConfiguration configuration)
    {
        _senderEmail = configuration["Smtp:SenderEmail"] ?? "";
        _senderPassword = configuration["Smtp:SenderPassword"] ?? "";
        _useRealSmtp = bool.TryParse(configuration["Smtp:UseRealSmtp"], out var b) ? b : false;
    }

    public string GenerateVerificationCode()
    {
        int num = RandomNumberGenerator.GetInt32(100000, 1000000);
        return num.ToString();
    }

    public async Task<bool> SendVerificationCodeAsync(string toEmail, string code)
    {
        // ВАЖНО: код — это 2FA-credential. Логируем ТОЛЬКО в dev-режиме.
        if (!_useRealSmtp || string.IsNullOrWhiteSpace(_senderEmail) || string.IsNullOrWhiteSpace(_senderPassword))
        {
            Log.Warning("[DEV MODE] Код подтверждения для {MaskedEmail}: {Code}. SMTP отключён.",
                PiiMasking.Email(toEmail), code);
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
            Log.Information("Письмо с кодом отправлено на {MaskedEmail}", PiiMasking.Email(toEmail));
            return true;
        }
        catch (Exception ex)
        {
            // НЕ логируем код в тексте ошибки — ex.Message может содержать тело письма
            Log.Error(ex, "Ошибка отправки письма на {MaskedEmail}", PiiMasking.Email(toEmail));
            return false;
        }
    }
}