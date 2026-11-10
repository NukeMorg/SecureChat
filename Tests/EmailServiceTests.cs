using Microsoft.Extensions.Options;
using SecureChat.Server;
using SecureChat.Server.Auth;
using Xunit;

namespace SecureChat.Tests;

/// <summary>Тесты EmailService: генерация кода подтверждения и dev-режим (без реального SMTP).</summary>
public class EmailServiceTests
{
    private static SmtpOptions DevOptions() => new()
    {
        UseRealSmtp = false,
        SenderEmail = "",
        SenderPassword = ""
    };

    private static EmailService CreateDevModeService() =>
        new(Options.Create(DevOptions()));

    [Fact]
    public void GenerateVerificationCode_IsSixDigits()
    {
        var service = CreateDevModeService();
        for (int i = 0; i < 50; i++)
        {
            string code = service.GenerateVerificationCode();
            Assert.Matches(@"^\d{6}$", code);
        }
    }

    [Fact]
    public void GenerateVerificationCode_ProducesDifferentCodes()
    {
        var service = CreateDevModeService();
        var codes = new HashSet<string>(Enumerable.Range(0, 40).Select(_ => service.GenerateVerificationCode()));
        // CSPRNG: 40 подряд одинаковых 6-значных кодов практически невозможны
        Assert.True(codes.Count > 1);
    }

    [Fact]
    public async Task SendVerificationCodeAsync_DevModeWithoutCredentials_ReturnsTrue()
    {
        var service = CreateDevModeService();
        bool sent = await service.SendVerificationCodeAsync("user@example.com", "123456");
        Assert.True(sent);
    }

    [Fact]
    public async Task SendVerificationCodeAsync_RealSmtpButEmptyCredentials_DoesNotThrowAndReturnsTrue()
    {
        // незаполненные учётные данные при включённом реальном SMTP переводят сервис в dev-режим
        var service = new EmailService(Options.Create(new SmtpOptions
        {
            UseRealSmtp = true,
            SenderEmail = "",
            SenderPassword = ""
        }));

        bool sent = await service.SendVerificationCodeAsync("user@example.com", "654321");
        Assert.True(sent);
    }
}
