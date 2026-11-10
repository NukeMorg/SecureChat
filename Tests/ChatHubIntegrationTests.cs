using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecureChat.Core;
using SecureChat.Server;
using SecureChat.Server.Auth;
using Xunit;

namespace SecureChat.Tests;

[Collection(nameof(ServerDatabaseCollection))]
public class ChatHubIntegrationTests : IAsyncLifetime
{
    private TempDbDirectory _tempDir = null!;
    private IDatabase _db = null!;
    private ConnectionRegistry _registry = null!;
    private IEmailService _email = null!;
    private IJwtTokenService _tokens = null!;
    private ISessionRepository _sessions = null!;
    private WebApplication _app = null!;
    private string _serverUrl = null!;
    private string _hubUrl = null!;

    public async Task InitializeAsync()
    {
        _tempDir = new TempDbDirectory();
        _db = new Database(_tempDir.ConnectionString);
        _db.Initialize();

        _registry = new ConnectionRegistry();
        _email = new EmailService(Microsoft.Extensions.Options.Options.Create(new SecureChat.Server.Auth.SmtpOptions
        {
            UseRealSmtp = false,
            SenderEmail = "",
            SenderPassword = ""
        }));
        _tokens = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(new SecureChat.Server.Auth.JwtOptions
        {
            Secret = "integration-test-secret-0123456789abcdef-0123456789abcdef"
        }));
        _sessions = new SessionRepository(_db.ConnectionString, _tokens);
        _sessions.Initialize();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(_db);
        builder.Services.AddSingleton(_registry);
        builder.Services.AddSingleton(_email);
        builder.Services.AddSingleton(_tokens);
        builder.Services.AddSingleton(_sessions);
        builder.Services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 20 * 1024 * 1024;
        });
        builder.Logging.ClearProviders();

        _app = builder.Build();
        _app.MapHub<ChatHub>("/hub");
        _app.MapGet("/health", () => "Healthy");
        _app.MapGet("/", () => "SecureChat Server is Running");

        await _app.StartAsync();

        var addressFeature = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _serverUrl = addressFeature!.Addresses.First();
        _hubUrl = $"{_serverUrl}/hub";
    }

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        _tempDir.Dispose();
    }

    private HubConnection CreateConnection() =>
        new HubConnectionBuilder()
            .WithUrl(_hubUrl)
            .Build();

    /// <summary>Регистрация + выпуск JWT-сессии (как это делает ChatHub при входе).</summary>
    private (int id, string token) RegisterAndIssue(string email, string password, string name, string publicKey)
    {
        int id = _db.RegisterUser(email, password, name, publicKey)
            ?? throw new InvalidOperationException($"RegisterUser failed for {email}");
        var issued = _tokens.Issue(id, name, "Employee");
        _sessions.Create(id, issued);
        return (id, issued.Token);
    }

    private string? GetLatestVerificationCode(string email)
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Code FROM EmailVerifications WHERE LOWER(Email) = @email";
        cmd.Parameters.AddWithValue("@email", email.Trim().ToLowerInvariant());
        return (string?)cmd.ExecuteScalar();
    }

    private static async Task<T> AwaitEventAsync<T>(HubConnection connection, string eventName, Func<Task> trigger, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = connection.On<T>(eventName, payload => tcs.TrySetResult(payload));
        await trigger();
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (completed != tcs.Task)
            throw new TimeoutException($"Таймаут ожидания события '{eventName}'");
        return await tcs.Task;
    }

    // 2. Регистрация: SendEmailCode -> код из БД -> RegisterAccount -> Ok + токен + роль Employee
    [Fact]
    public async Task Registration_SendEmailCode_And_RegisterAccount_Succeeds()
    {
        await using var client = CreateConnection();
        await client.StartAsync();

        string email = "newuser@test.local";
        var sendCodeResp = await AwaitEventAsync<SendEmailCodeResponsePayload>(
            client,
            "SendEmailCodeResponse",
            () => client.SendAsync("SendEmailCode", new SendEmailCodePayload { Email = email }));

        Assert.Equal("Ok", sendCodeResp.Status);

        string? code = GetLatestVerificationCode(email);
        Assert.NotNull(code);
        Assert.Matches(@"^\d{6}$", code);

        var regResp = await AwaitEventAsync<RegisterAccountResponsePayload>(
            client,
            "RegisterAccountResponse",
            () => client.SendAsync("RegisterAccount", new RegisterAccountPayload
            {
                Email = email,
                Password = "StrongPassword1!",
                UserName = "NewUser",
                PublicKey = "-----BEGIN PUBLIC KEY-----TEST-----END PUBLIC KEY-----",
                Code = code
            }));

        Assert.Equal("Ok", regResp.Status);
        Assert.True(regResp.AssignedId > 0);
        Assert.False(string.IsNullOrEmpty(regResp.Token));
        Assert.Equal(2, regResp.Token.Count(c => '.' == c)); // JWT: header.payload.signature
        Assert.Equal("Employee", regResp.Role);
    }

    // 3. Login неверный пароль -> InvalidCredentials; верный -> Ok; Hello с токеном -> Ok; Hello невалидный токен -> отклонён
    [Fact]
    public async Task Login_And_Hello_Scenarios()
    {
        var (userId, token) = RegisterAndIssue("auth@test.local", "CorrectPass1!", "AuthUser", "PK");

        await using var client = CreateConnection();
        await client.StartAsync();

        // 3a. Неверный пароль
        var wrongPassResp = await AwaitEventAsync<LoginResponsePayload>(
            client,
            "LoginResponse",
            () => client.SendAsync("Login", new LoginPayload { Email = "auth@test.local", Password = "WrongPassword!" }));
        Assert.Equal("InvalidCredentials", wrongPassResp.Status);

        // 3b. Верный пароль
        var correctPassResp = await AwaitEventAsync<LoginResponsePayload>(
            client,
            "LoginResponse",
            () => client.SendAsync("Login", new LoginPayload { Email = "auth@test.local", Password = "CorrectPass1!" }));
        Assert.Equal("Ok", correctPassResp.Status);
        Assert.Equal(userId, correctPassResp.UserId);
        Assert.False(string.IsNullOrEmpty(correctPassResp.Token));
        Assert.Equal(2, correctPassResp.Token.Count(c => '.' == c)); // JWT: header.payload.signature

        // 3c. Hello с валидным токеном на новом подключении
        await using var client2 = CreateConnection();
        await client2.StartAsync();

        var helloResp = await AwaitEventAsync<HelloResponsePayload>(
            client2,
            "HelloResponse",
            () => client2.SendAsync("Hello", new HelloPayload { Token = token }));
        Assert.Equal("Ok", helloResp.Status);
        Assert.Equal(userId, helloResp.UserId);

        // 3d. Hello с невалидным токеном
        await using var client3 = CreateConnection();
        await client3.StartAsync();

        var errorResp = await AwaitEventAsync<SimpleStatusPayload>(
            client3,
            "Error",
            () => client3.SendAsync("Hello", new HelloPayload { Token = "invalid_token_12345" }));
        Assert.Equal("UnknownClient", errorResp.Status);
    }

    // 4. Создание канала: Employee -> Forbidden; роль Manager -> Ok; дубль имени -> NameExists; пустое имя -> InvalidName;
    //    не-участник не видит канал, после AddChannelMember -> видит
    [Fact]
    public async Task Channels_Lifecycle_Roles_And_Membership()
    {
        var (employeeId, empToken) = RegisterAndIssue("emp@test.local", "Pass1234!", "Emp", "PK1");
        var (otherId, otherToken) = RegisterAndIssue("other@test.local", "Pass1234!", "Other", "PK2");

        await using var empClient = CreateConnection();
        await empClient.StartAsync();
        await AwaitEventAsync<HelloResponsePayload>(
            empClient,
            "HelloResponse",
            () => empClient.SendAsync("Hello", new HelloPayload { Token = empToken }));

        // 4a. Employee пытается создать канал -> Forbidden
        var forbiddenResp = await empClient.InvokeAsync<CreateChannelResponsePayload>(
            "CreateChannel",
            new CreateChannelPayload { Name = "Запретный канал" });
        Assert.Equal("Forbidden", forbiddenResp.Status);

        // 4b. Повышаем роль до Manager
        _db.SetUserRole(employeeId, "Manager");

        // 4c. Пустое имя -> InvalidName
        var invalidNameResp = await empClient.InvokeAsync<CreateChannelResponsePayload>(
            "CreateChannel",
            new CreateChannelPayload { Name = "   " });
        Assert.Equal("InvalidName", invalidNameResp.Status);

        // 4d. Создание канала менеджером -> Ok
        var createOkResp = await empClient.InvokeAsync<CreateChannelResponsePayload>(
            "CreateChannel",
            new CreateChannelPayload { Name = "Общий канал" });
        Assert.Equal("Ok", createOkResp.Status);
        string channelId = createOkResp.ChannelId!;
        Assert.False(string.IsNullOrEmpty(channelId));

        // 4e. Дубль имени -> NameExists
        var dupResp = await empClient.InvokeAsync<CreateChannelResponsePayload>(
            "CreateChannel",
            new CreateChannelPayload { Name = "Общий канал" });
        Assert.Equal("NameExists", dupResp.Status);

        // 4f. Менеджер видит канал в GetChannels
        var channelsList = await empClient.InvokeAsync<ChannelListPayload>("GetChannels");
        Assert.Contains(channelsList.Channels, c => c.Id == channelId && c.Name == "Общий канал");

        // 4g. Другой пользователь подключается и не видит канал
        await using var otherClient = CreateConnection();
        await otherClient.StartAsync();
        await AwaitEventAsync<HelloResponsePayload>(
            otherClient,
            "HelloResponse",
            () => otherClient.SendAsync("Hello", new HelloPayload { Token = otherToken }));

        var otherListBefore = await otherClient.InvokeAsync<ChannelListPayload>("GetChannels");
        Assert.DoesNotContain(otherListBefore.Channels, c => c.Id == channelId);

        // 4h. Добавляем пользователя в канал
        var addMemberResp = await empClient.InvokeAsync<SimpleStatusPayload>(
            "AddChannelMember",
            new AddChannelMemberPayload { ChannelId = channelId, UserId = otherId });
        Assert.Equal("Ok", addMemberResp.Status);

        // 4i. Теперь другой пользователь видит канал
        var otherListAfter = await otherClient.InvokeAsync<ChannelListPayload>("GetChannels");
        Assert.Contains(otherListAfter.Channels, c => c.Id == channelId);
    }

    // 5. Прямые сообщения: IncomingMessage у получателя, Ack -> DeliveryStatus у отправителя
    [Fact]
    public async Task DirectMessages_Sending_IncomingMessage_And_AckDeliveryStatus()
    {
        var (aliceId, aliceToken) = RegisterAndIssue("alice@test.local", "Pass1234!", "Alice", "PKA");
        var (bobId, bobToken) = RegisterAndIssue("bob@test.local", "Pass1234!", "Bob", "PKB");

        await using var aliceClient = CreateConnection();
        await aliceClient.StartAsync();
        await AwaitEventAsync<HelloResponsePayload>(
            aliceClient,
            "HelloResponse",
            () => aliceClient.SendAsync("Hello", new HelloPayload { Token = aliceToken }));

        await using var bobClient = CreateConnection();
        await bobClient.StartAsync();
        await AwaitEventAsync<HelloResponsePayload>(
            bobClient,
            "HelloResponse",
            () => bobClient.SendAsync("Hello", new HelloPayload { Token = bobToken }));

        string msgId = "direct-msg-test-1";
        var incomingTcs = new TaskCompletionSource<IncomingMessagePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        bobClient.On<IncomingMessagePayload>("IncomingMessage", payload => incomingTcs.TrySetResult(payload));

        var sendPayload = new SendMessagePayload
        {
            ReceiverId = bobId,
            MessageId = msgId,
            EncryptedKey = "enc-key",
            EncryptedKeySelf = "enc-key-self",
            EncryptedData = new EncryptedData
            {
                Nonce = "nonce123",
                Ciphertext = "ciphertext123",
                Tag = "tag123"
            },
            ContentType = "Text"
        };

        await aliceClient.SendAsync("SendMessage", sendPayload);

        var incoming = await incomingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(msgId, incoming.MessageId);
        Assert.Equal(aliceId, incoming.SenderId);
        Assert.Equal("ciphertext123", incoming.EncryptedData?.Ciphertext);

        // Теперь Bob отправляет Ack "Delivered" -> Alice должна получить DeliveryStatus "Delivered"
        var deliveryTcs = new TaskCompletionSource<DeliveryStatusPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        aliceClient.On<DeliveryStatusPayload>("DeliveryStatus", payload =>
        {
            if (payload.MessageId == msgId && payload.Status == "Delivered")
                deliveryTcs.TrySetResult(payload);
        });

        await bobClient.SendAsync("Ack", new AckPayload { MessageId = msgId, Status = "Delivered" });
        var delivery = await deliveryTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(msgId, delivery.MessageId);
        Assert.Equal("Delivered", delivery.Status);
    }

    // 6. Сообщения канала: участникам приходит IncomingChannelMessage
    [Fact]
    public async Task ChannelMessages_BroadcastToMembers()
    {
        var (ownerId, ownerToken) = RegisterAndIssue("ch-owner@test.local", "Pass1234!", "Owner", "PKO");
        var (memberId, memberToken) = RegisterAndIssue("ch-member@test.local", "Pass1234!", "Member", "PKM");
        _db.SetUserRole(ownerId, "Manager");

        string channelId = _db.CreateChannel("Канал рассылки", ownerId)!;
        _db.AddChannelMemberToChannel(channelId, memberId);

        await using var ownerClient = CreateConnection();
        await ownerClient.StartAsync();
        await AwaitEventAsync<HelloResponsePayload>(
            ownerClient,
            "HelloResponse",
            () => ownerClient.SendAsync("Hello", new HelloPayload { Token = ownerToken }));

        await using var memberClient = CreateConnection();
        await memberClient.StartAsync();
        await AwaitEventAsync<HelloResponsePayload>(
            memberClient,
            "HelloResponse",
            () => memberClient.SendAsync("Hello", new HelloPayload { Token = memberToken }));

        var channelMsgTcs = new TaskCompletionSource<IncomingChannelMessagePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        memberClient.On<IncomingChannelMessagePayload>("IncomingChannelMessage", payload => channelMsgTcs.TrySetResult(payload));

        string msgId = "ch-broadcast-1";
        await ownerClient.SendAsync("SendChannelMessage", new SendChannelMessagePayload
        {
            ChannelId = channelId,
            MessageId = msgId,
            ChainIteration = 0,
            EncryptedData = new EncryptedData
            {
                Nonce = "ch-nonce",
                Ciphertext = "ch-ciphertext",
                Tag = "ch-tag"
            },
            ContentType = "Text"
        });

        var received = await channelMsgTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(msgId, received.MessageId);
        Assert.Equal(channelId, received.ChannelId);
        Assert.Equal(ownerId, received.SenderId);
        Assert.Equal("ch-ciphertext", received.EncryptedData?.Ciphertext);
    }

    // 7. Logout -> токен отозван; забаненный юзер -> Login Banned
    [Fact]
    public async Task Logout_RevokesSession_And_BannedUserCannotLogin()
    {
        var (userId, token) = RegisterAndIssue("ban@test.local", "Pass1234!", "BanUser", "PK");

        await using var client = CreateConnection();
        await client.StartAsync();

        // 7a. Вход
        var loginResp = await AwaitEventAsync<LoginResponsePayload>(
            client,
            "LoginResponse",
            () => client.SendAsync("Login", new LoginPayload { Email = "ban@test.local", Password = "Pass1234!" }));
        Assert.Equal("Ok", loginResp.Status);
        string sessionToken = loginResp.Token;

        // Сессия валидна
        Assert.NotNull(_sessions.Validate(sessionToken));

        // 7b. Logout
        var logoutResp = await AwaitEventAsync<SimpleStatusPayload>(
            client,
            "LogoutResponse",
            () => client.SendAsync("Logout"));
        Assert.Equal("Ok", logoutResp.Status);

        // Сессия отозвана
        Assert.Null(_sessions.Validate(sessionToken));

        // 7c. Баним пользователя в БД
        _db.SetUserActive(userId, false);

        await using var client2 = CreateConnection();
        await client2.StartAsync();

        var bannedLoginResp = await AwaitEventAsync<LoginResponsePayload>(
            client2,
            "LoginResponse",
            () => client2.SendAsync("Login", new LoginPayload { Email = "ban@test.local", Password = "Pass1234!" }));
        Assert.Equal("Banned", bannedLoginResp.Status);
    }
}
