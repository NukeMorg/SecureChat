using System;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using SecureChat.Server.Auth;
using SecureChat.Security;
using Serilog;
using ILogger = Serilog.ILogger;

namespace SecureChat.Server;

/// <summary>Отправка кодов подтверждения: реальный SMTP или dev-режим (код печатается в лог).</summary>
public class EmailService : IEmailService
{
    private readonly SmtpOptions _options;

    private static readonly ILogger Log = Serilog.Log.ForContext<EmailService>();

    public EmailService(IOptions<SmtpOptions> options) => _options = options.Value;

    public string GenerateVerificationCode()
    {
        int num = RandomNumberGenerator.GetInt32(100000, 1000000);
        return num.ToString();
    }

    public async Task<bool> SendVerificationCodeAsync(string toEmail, string code)
    {
        // ВАЖНО: код — это 2FA-credential. Логируем ТОЛЬКО в dev-режиме.
        if (!_options.UseRealSmtp || string.IsNullOrWhiteSpace(_options.SenderEmail) || string.IsNullOrWhiteSpace(_options.SenderPassword))
        {
            Log.Warning("[DEV MODE] Verification code for {MaskedEmail}: {Code}. SMTP disabled.",
                PiiMasker.Email(toEmail), code);
            return true;
        }

        try
        {
            using var client = new SmtpClient(_options.Host, _options.Port)
            {
                Credentials = new NetworkCredential(_options.SenderEmail, _options.SenderPassword),
                EnableSsl = true,
                Timeout = 10000
            };

            var mail = new MailMessage
            {
                From = new MailAddress(_options.SenderEmail, "SecureChat Enterprise"),
                Subject = "Код подтверждения SecureChat",
                Body = $"Здравствуйте!\n\nВаш проверочный код для SecureChat: {code}\nКод действителен 10 минут.",
                IsBodyHtml = false
            };
            mail.To.Add(toEmail);

            await client.SendMailAsync(mail);
            Log.Information("Verification email sent to {MaskedEmail}", PiiMasker.Email(toEmail));
            return true;
        }
        catch (Exception ex)
        {
            // НЕ логируем код в тексте ошибки — ex.Message может содержать тело письма
            Log.Error(ex, "Failed to send verification email to {MaskedEmail}", PiiMasker.Email(toEmail));
            return false;
        }
    }
}
