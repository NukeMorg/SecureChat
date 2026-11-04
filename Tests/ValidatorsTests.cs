using SecureChat.Core;
using Xunit;

namespace SecureChat.Tests;

/// <summary>Тесты валидаторов (Лаба 2): email, пароль, имя, управляющие символы.</summary>
public class ValidatorsTests
{
    [Theory]
    [InlineData("user@gmail.com")]
    [InlineData("first.last@company.com")]
    [InlineData("a_b%c+d@sub.domain.org")]
    public void IsValidEmail_AcceptsValidEmails(string email) =>
        Assert.True(Validators.IsValidEmail(email));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("two@@at.com")]
    [InlineData("@nodomain.com")]
    [InlineData("user@nodot")]
    [InlineData("user@.com")]
    [InlineData("кириллица@маил.рф")]
    public void IsValidEmail_RejectsInvalidEmails(string email) =>
        Assert.False(Validators.IsValidEmail(email));

    [Theory]
    [InlineData("Passw0rd")]
    [InlineData("abcdefg1")]
    [InlineData("12345678a")]
    public void IsValidPassword_AcceptsStrongEnough(string password) =>
        Assert.True(Validators.IsValidPassword(password));

    [Theory]
    [InlineData("")]
    [InlineData("A1")]           // короче 8
    [InlineData("abcdefgh")]     // без цифры
    [InlineData("12345678")]     // без буквы
    public void IsValidPassword_RejectsWeak(string password) =>
        Assert.False(Validators.IsValidPassword(password));

    [Fact]
    public void IsValidUserName_AcceptsNormalName() =>
        Assert.True(Validators.IsValidUserName("Иван Петров"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidUserName_RejectsEmpty(string? name) =>
        Assert.False(Validators.IsValidUserName(name));

    [Fact]
    public void IsValidUserName_RejectsTooLong()
    {
        var longName = new string('a', 101);
        Assert.False(Validators.IsValidUserName(longName));
        Assert.True(Validators.IsValidUserName(new string('a', 100)));
    }

    [Fact]
    public void HasControlCharacters_DetectsControlButAllowsNewlineAndTab()
    {
        Assert.False(Validators.HasControlCharacters("обычный текст\nс переносом\tи табом"));
        Assert.True(Validators.HasControlCharacters("bad\x01char"));
        Assert.True(Validators.HasControlCharacters("null\0char"));
    }

    [Fact]
    public void IsValidMessageText_ChecksLengthAndControls()
    {
        Assert.True(Validators.IsValidMessageText("Привет!"));
        Assert.False(Validators.IsValidMessageText(""));
        Assert.False(Validators.IsValidMessageText(new string('x', 4001)));
        Assert.True(Validators.IsValidMessageText(new string('x', 4000)));
        Assert.False(Validators.IsValidMessageText("bad\x02"));
    }
}

/// <summary>Тесты маскирования PII (требование 1.7.2).</summary>
public class PiiMaskerTests
{
    [Fact]
    public void Email_MasksLocalPartKeepsDomain()
    {
        Assert.Equal("u***@company.com", SecureChat.Security.PiiMasker.Email("user@company.com"));
        Assert.Equal("a***@mail.ru", SecureChat.Security.PiiMasker.Email("admin@mail.ru"));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("@nodomain")]
    public void Email_HandlesBrokenInput(string email) =>
        Assert.Equal("***", SecureChat.Security.PiiMasker.Email(email));

    [Fact]
    public void Email_EmptyInput_YieldsEmpty() =>
        Assert.Equal("", SecureChat.Security.PiiMasker.Email(""));

    [Fact]
    public void Phone_KeepsOnlyLastTwoDigits()
    {
        string masked = SecureChat.Security.PiiMasker.Phone("+7 912 345-67-89");
        Assert.Equal("*********89", masked);
        Assert.DoesNotContain("345", masked);
    }

    [Fact]
    public void Token_ShowsOnlyEdges()
    {
        string token = "abcdefghijklmnop";
        string masked = SecureChat.Security.PiiMasker.Token(token);
        Assert.Equal("abcd***mnop", masked);
    }

    [Fact]
    public void Token_MasksShortFully() =>
        Assert.Equal("***", SecureChat.Security.PiiMasker.Token("short"));

    [Fact]
    public void Token_EmptyInput_YieldsEmpty() =>
        Assert.Equal("", SecureChat.Security.PiiMasker.Token(""));
}

/// <summary>Тесты агрегатора (требование 1.5.3): один расчёт для Excel и Word.</summary>
public class ChatAggregatorTests
{
    private static ChatExportRow Row(string sender, string text, int hour) =>
        new() { Id = Guid.NewGuid().ToString(), Sender = sender, Text = text, TimestampUtc = new DateTime(2026, 1, 1, hour, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public void Aggregate_ComputesTotalsTopUserAndByHour()
    {
        var stats = ChatAggregator.Aggregate(new[]
        {
            Row("Аня", "привет", 9),
            Row("Борис", "привет!", 9),
            Row("Аня", "как дела", 10),
            Row("Аня", "ок", 22)
        });

        Assert.Equal(4, stats.TotalMessages);
        Assert.Equal(2, stats.ActiveUsers);
        Assert.Equal("Аня", stats.TopUser);
        Assert.Equal(2, stats.ByHour.First(h => h.Key == "09:00").Value);
        Assert.Equal(1, stats.ByHour.First(h => h.Key == "22:00").Value);
    }

    [Fact]
    public void Aggregate_EmptyInput_YieldsZeroes()
    {
        var stats = ChatAggregator.Aggregate(Array.Empty<ChatExportRow>());
        Assert.Equal(0, stats.TotalMessages);
        Assert.Equal(0, stats.ActiveUsers);
        Assert.Equal("", stats.TopUser);
        Assert.Empty(stats.ByHour);
    }

    [Fact]
    public void Aggregate_CapsAtMaxRows()
    {
        var many = Enumerable.Range(0, ChatAggregator.MaxRows + 500)
            .Select(i => Row($"u{i % 3}", $"msg {i}", 12));
        var stats = ChatAggregator.Aggregate(many);
        Assert.Equal(ChatAggregator.MaxRows, stats.TotalMessages);
    }
}

/// <summary>Formula injection и имена файлов (требования 1.5.1).</summary>
public class SanitizerTests
{
    [Theory]
    [InlineData("=cmd()", "'=cmd()")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2", "'-2")]
    [InlineData("@sum", "'@sum")]
    [InlineData("\ttab", "'\ttab")]
    [InlineData("\rcr", "'\rcr")]
    public void CellText_NeutralizesDangerousPrefixes(string input, string expected) =>
        Assert.Equal(expected, CellText.Sanitize(input));

    [Fact]
    public void CellText_KeepsSafeTextUnchanged() =>
        Assert.Equal("обычный текст", CellText.Sanitize("обычный текст"));

    [Theory]
    [InlineData("file:name?.xlsx", "filename.xlsx")]
    [InlineData("report 2026", "report 2026")]
    public void PathSanitizer_StripsInvalidCharacters(string input, string expected) =>
        Assert.Equal(expected, PathSanitizer.SanitizeFileName(input));

    [Fact]
    public void PathSanitizer_FallsBackOnEmpty() =>
        Assert.Equal("export", PathSanitizer.SanitizeFileName("???"));
}
