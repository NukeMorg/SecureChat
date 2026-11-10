using Microsoft.Extensions.Configuration;
using SecureChat.Server;
using Xunit;

namespace SecureChat.Tests;

/// <summary>Тесты EmailService: генерация кода подтверждения и dev-режим (без реального SMTP).</summary>
public class EmailServiceTests
{
    private static EmailService CreateDevModeService() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:UseRealSmtp"] = "false",
                ["Smtp:SenderEmail"] = "",
                ["Smtp:SenderPassword"] = ""
            })
            .Build());

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
    public void GenerateVerificationCode_AlwaysInRange()
    {
        var service = CreateDevModeService();
        for (int i = 0; i < 100; i++)
        {
            int code = int.Parse(service.GenerateVerificationCode());
            Assert.InRange(code, 100000, 999999);
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
        var service = new EmailService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:UseRealSmtp"] = "true",
                ["Smtp:SenderEmail"] = "",
                ["Smtp:SenderPassword"] = ""
            })
            .Build());

        bool sent = await service.SendVerificationCodeAsync("user@example.com", "654321");
        Assert.True(sent);
    }
}
