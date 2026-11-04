using Microsoft.AspNetCore.SignalR;
using SecureChat.Core;
using Serilog;
using ILogger = Serilog.ILogger;
using Serilog.Context;
using System.Diagnostics;

namespace SecureChat.Server;

/// <summary>
/// Хаб Direct-чатов. Аутентификация выполняется методами Login/RegisterAccount/Hello,
/// связка Context.ConnectionId ↔ UserId ведётся в ConnectionRegistry.
/// </summary>
public class ChatHub : Hub
{
    private readonly Database _db;
    private readonly EmailService _email;
    private readonly ConnectionRegistry _registry;
    private readonly ILogger _log = Serilog.Log.ForContext<ChatHub>();

    public ChatHub(Database db, EmailService email, ConnectionRegistry registry)
    {
        _db = db;
        _email = email;
        _registry = registry;
    }

    private int? UserId => _registry.GetUserId(Context.ConnectionId);

    private const string SessionTokenKey = "SessionToken";

    private void RememberSessionToken(string? token)
    {
        if (!string.IsNullOrEmpty(token)) Context.Items[SessionTokenKey] = token;
    }

    // ---------- Аутентификация ----------

    public async Task SendEmailCode(SendEmailCodePayload? p)
    {
        if (p == null || !Validators.IsValidEmail(p.Email))
        {
            await Respond("SendEmailCodeResponse",
                new SendEmailCodeResponsePayload { Status = "InvalidEmail", Message = "Некорректный адрес почты." });
            return;
        }

        if (!_db.TryReserveEmailSend(p.Email, TimeSpan.FromMinutes(15), maxAttempts: 3))
        {
            _log.Warning("Email code rate limit for {MaskedEmail}", PiiMasking.Email(p.Email));
            await Respond("SendEmailCodeResponse",
                new SendEmailCodeResponsePayload { Status = "RateLimit", Message = "Слишком много запросов. Попробуйте через 15 минут." });
            return;
        }

        string code = _email.GenerateVerificationCode();
        _db.SaveVerificationCode(p.Email, code, TimeSpan.FromMinutes(10));

        bool sent = await _email.SendVerificationCodeAsync(p.Email, code);
        if (sent)
        {
            _log.Information("Verification code sent to {MaskedEmail}", PiiMasking.Email(p.Email));
            await Respond("SendEmailCodeResponse",
                new SendEmailCodeResponsePayload { Status = "Ok", Message = "Код подтверждения отправлен на почту." });
        }
        else
        {
            _log.Error("Failed to send email to {MaskedEmail}", PiiMasking.Email(p.Email));
            await Respond("SendEmailCodeResponse",
                new SendEmailCodeResponsePayload { Status = "SendFailed", Message = "Не удалось отправить письмо." });
        }
    }

    public async Task Login(LoginPayload? p)
    {
        // нейтральный ответ без раскрытия причины (анти-enumeration)
        if (p == null || !Validators.IsValidEmail(p.Email))
        {
            await Respond("LoginResponse", new LoginResponsePayload { Status = "InvalidCredentials" });
            return;
        }

        _log.Information("Login attempt for {MaskedEmail}", PiiMasking.Email(p.Email));

        string ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_db.IsLoginBlocked(p.Email, ip, TimeSpan.FromMinutes(15), maxAttempts: 5))
        {
            _log.Warning("Login rate limit exceeded for {MaskedEmail} ip={Ip}", PiiMasking.Email(p.Email), ip);
            await Respond("LoginResponse", new LoginResponsePayload { Status = "RateLimited" });
            return;
        }

        var auth = _db.Authenticate(p.Email, p.Password);
        if (auth == null)
        {
            _db.RecordFailedLogin(p.Email, ip);
            _log.Warning("Invalid credentials for {MaskedEmail}", PiiMasking.Email(p.Email));
            await Respond("LoginResponse", new LoginResponsePayload { Status = "InvalidCredentials" });
            return;
        }

        int userId = auth.Value.id;
        _registry.Register(userId, Context.ConnectionId);
        RememberSessionToken(auth.Value.token);

        _log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} success=true", "Login", userId);
        _log.Information("Login successful userId={UserId}", userId);

        await Respond("LoginResponse",
            new LoginResponsePayload { Status = "Ok", UserId = userId, UserName = auth.Value.name, Token = auth.Value.token });

        await NotifyOnlineStatus(userId);
        await PushPendingMessages(userId);
    }

    public async Task RegisterAccount(RegisterAccountPayload? p)
    {
        if (p == null || !Validators.IsValidEmail(p.Email))
        {
            await Respond("RegisterAccountResponse", new RegisterAccountResponsePayload { Status = "InvalidEmail" });
            return;
        }
        if (!Validators.IsValidPassword(p.Password))
        {
            await Respond("RegisterAccountResponse", new RegisterAccountResponsePayload { Status = "WeakPassword" });
            return;
        }
        if (!Validators.IsValidUserName(p.UserName))
        {
            await Respond("RegisterAccountResponse", new RegisterAccountResponsePayload { Status = "InvalidUserName" });
            return;
        }

        _log.Information("Registration request for {MaskedEmail}", PiiMasking.Email(p.Email));

        var verifyResult = _db.VerifyAndConsumeCode(p.Email, p.Code);
        if (verifyResult == Database.CodeVerifyResult.Invalid)
        {
            _log.Warning("Invalid verification code for {MaskedEmail}", PiiMasking.Email(p.Email));
            await Respond("RegisterAccountResponse", new RegisterAccountResponsePayload { Status = "InvalidCode" });
            return;
        }
        if (verifyResult == Database.CodeVerifyResult.Expired)
        {
            _log.Warning("Verification code expired for {MaskedEmail}", PiiMasking.Email(p.Email));
            await Respond("RegisterAccountResponse", new RegisterAccountResponsePayload { Status = "CodeExpired" });
            return;
        }

        var reg = _db.RegisterUser(p.Email, p.Password, p.UserName, p.PublicKey);
        if (reg == null)
        {
            _log.Warning("User already exists: {MaskedEmail}", PiiMasking.Email(p.Email));
            await Respond("RegisterAccountResponse", new RegisterAccountResponsePayload { Status = "UserExists" });
            return;
        }

        int userId = reg.Value.id;
        _registry.Register(userId, Context.ConnectionId);
        RememberSessionToken(reg.Value.token);

        _log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} success=true", "Register", userId);
        _log.Information("New user registered userId={UserId}", userId);

        await Respond("RegisterAccountResponse",
            new RegisterAccountResponsePayload { Status = "Ok", AssignedId = userId, Token = reg.Value.token });

        await NotifyOnlineStatus(userId);
    }

    public async Task Hello(HelloPayload? p)
    {
        if (p == null) return;

        // Идентификация только по токену сессии: знание UserId не даёт доступа.
        int? userId = null;
        if (!string.IsNullOrEmpty(p.Token)) userId = _db.ValidateSession(p.Token);

        if (userId == null)
        {
            _log.Warning("Hello rejected: invalid or expired token");
            await Respond("Error", new SimpleStatusPayload { Status = "UnknownClient" });
            return;
        }

        _registry.Register(userId.Value, Context.ConnectionId);
        RememberSessionToken(p.Token);
        string name = _db.GetUserName(userId.Value) ?? $"User {userId.Value}";

        _log.Information("Hello: reconnect userId={UserId}", userId.Value);

        await Respond("HelloResponse",
            new HelloResponsePayload { Status = "Ok", UserId = userId.Value, UserName = name });

        await NotifyOnlineStatus(userId.Value);
        await PushPendingMessages(userId.Value);
    }

    public async Task Logout()
    {
        if (_registry.GetUserId(Context.ConnectionId) is not int uid) return;

        if (Context.Items.TryGetValue(SessionTokenKey, out var tokenObj) && tokenObj is string token)
            _db.RevokeSession(token);

        _registry.Unregister(Context.ConnectionId, out _);
        Context.Items.Remove(SessionTokenKey);

        _log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId}", "Logout", uid);

        await Respond("LogoutResponse", new SimpleStatusPayload { Status = "Ok" });
        await Clients.AllExcept(Context.ConnectionId).SendAsync("UserOffline",
            new OnlineStatusPayload { UserId = uid });
        Context.Abort();
    }

    // ---------- Direct-чаты ----------

    public async Task SendMessage(SendMessagePayload? p)
    {
        int? senderId = UserId;
        if (senderId == null || p == null) return;

        using (LogContext.PushProperty("CorrelationId", p.MessageId))
        using (LogContext.PushProperty("MessageId", p.MessageId))
        {
            var sw = Stopwatch.StartNew();

            _db.SaveMessage(p.MessageId, senderId.Value, p.ReceiverId, p.EncryptedKey,
                p.EncryptedData?.Nonce, p.EncryptedData?.Ciphertext, p.EncryptedData?.Tag,
                p.ContentType, p.FileName, p.FileSize, p.FileTransferId, "Sent", p.EncryptedKeySelf);
            _db.AddContact(senderId.Value, p.ReceiverId);

            var receiverConns = _registry.GetConnections(p.ReceiverId);
            bool delivered = receiverConns.Count > 0;
            if (delivered)
            {
                await Clients.Clients(receiverConns).SendAsync("IncomingMessage", new IncomingMessagePayload
                {
                    SenderId = senderId.Value,
                    MessageId = p.MessageId,
                    EncryptedKey = p.EncryptedKey,
                    EncryptedData = p.EncryptedData,
                    ContentType = p.ContentType,
                    FileName = p.FileName,
                    FileSize = p.FileSize,
                    FileTransferId = p.FileTransferId
                });
                await Respond("DeliveryStatus", new DeliveryStatusPayload { MessageId = p.MessageId, Status = "Sent" });
            }
            else
            {
                await Respond("DeliveryStatus", new DeliveryStatusPayload { MessageId = p.MessageId, Status = "UserOffline" });
            }

            sw.Stop();
            var level = sw.ElapsedMilliseconds > 1000 ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Information;
            _log.Write(level, "Message from={Sender} to={Receiver} type={ContentType} size={Size}B delivered={Delivered} in {DurationMs}ms",
                senderId.Value, p.ReceiverId, p.ContentType, p.FileSize, delivered, sw.ElapsedMilliseconds);
        }
    }

    public async Task Ack(AckPayload? p)
    {
        if (UserId == null || p == null || p.Status != "Delivered") return;

        _db.UpdateMessageStatus(p.MessageId, "Delivered");
        var senderId = _db.GetSenderByMessageId(p.MessageId);
        if (senderId.HasValue)
        {
            var conns = _registry.GetConnections(senderId.Value);
            if (conns.Count > 0)
                await Clients.Clients(conns).SendAsync("DeliveryStatus",
                    new DeliveryStatusPayload { MessageId = p.MessageId, Status = "Delivered" });
        }
        _log.Debug("Ack Delivered messageId={MessageId}", p.MessageId);
    }

    public async Task GetHistory(GetHistoryRequestPayload? p)
    {
        int? userId = UserId;
        if (userId == null || p == null) return;
        var messages = _db.GetHistory(userId.Value, p.ContactId);
        _log.Debug("GetHistory userId={UserId} contactId={ContactId} count={Count}", userId.Value, p.ContactId, messages.Count);
        await Respond("HistoryResponse", new HistoryResponsePayload { ContactId = p.ContactId, Messages = messages });
    }

    // ---------- Профиль и контакты ----------

    public async Task GetContacts()
    {
        int? userId = UserId;
        if (userId == null) return;

        var contactIds = _db.GetContactIds(userId.Value);
        var contacts = new List<UserInfo>();
        foreach (var id in contactIds)
        {
            var name = _db.GetUserName(id) ?? $"User {id}";
            bool isOnline = _registry.IsOnline(id);
            contacts.Add(new UserInfo { Id = id, Name = name, IsOnline = isOnline });
        }
        _log.Debug("GetContacts userId={UserId} count={Count}", userId.Value, contacts.Count);
        await Respond("ContactList", new ContactListPayload { Contacts = contacts });
    }

    public async Task GetPublicKey(GetPublicKeyPayload? p)
    {
        if (p == null) return;
        string? key = _db.GetPublicKey(p.TargetId);
        await Respond("PublicKeyResponse",
            new PublicKeyResponsePayload { Status = key != null ? "Ok" : "NotFound", TargetId = p.TargetId, PublicKey = key ?? "" });
    }

    public async Task GetUserInfo(GetUserInfoPayload? p)
    {
        if (p == null) return;
        string? name = _db.GetUserName(p.TargetId);
        await Respond("UserInfoResponse",
            new UserInfoResponsePayload { Status = name != null ? "Ok" : "NotFound", UserId = p.TargetId, UserName = name ?? "" });
    }

    public async Task AddContact(AddContactPayload? p)
    {
        int? userId = UserId;
        if (userId == null || p == null) return;

        if (!_db.UserExists(p.ContactId))
        {
            _log.Warning("AddContact: contactId={ContactId} not found (userId={UserId})", p.ContactId, userId.Value);
            await Respond("AddContactResponse", new AddContactResponsePayload { Status = "NotFound", ContactId = p.ContactId });
            return;
        }
        _db.AddContact(userId.Value, p.ContactId);

        _log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} contactId={ContactId}",
            "AddContact", userId.Value, p.ContactId);

        await Respond("AddContactResponse", new AddContactResponsePayload { Status = "Ok", ContactId = p.ContactId });
        if (_registry.IsOnline(p.ContactId))
            await Respond("UserOnline", new OnlineStatusPayload { UserId = p.ContactId });
    }

    public async Task UpdateProfile(UpdateProfilePayload? p)
    {
        int? userId = UserId;
        if (userId == null || p == null) return;
        _db.UpdateUserName(userId.Value, p.UserName);

        _log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} newName={NewName}",
            "ProfileUpdate", userId.Value, p.UserName);

        var notification = new UserProfileUpdatedPayload { UserId = userId.Value, UserName = p.UserName };
        var contactConns = _db.GetContactIds(userId.Value).SelectMany(id => _registry.GetConnections(id)).ToList();
        if (contactConns.Count > 0)
            await Clients.Clients(contactConns).SendAsync("UserProfileUpdated", notification);
    }

    // ---------- Жизненный цикл соединения ----------

    public override Task OnConnectedAsync()
    {
        _log.Debug("Connected connectionId={ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_registry.Unregister(Context.ConnectionId, out int userId))
        {
            if (exception != null)
                _log.Warning(exception, "Connection dropped userId={UserId} connectionId={ConnectionId}",
                    userId, Context.ConnectionId);
            _log.ForContext("Audit", true).Information(
                "AUDIT {Action} userId={UserId} connectionId={ConnectionId}",
                "Logout", userId, Context.ConnectionId);
            await Clients.AllExcept(Context.ConnectionId).SendAsync("UserOffline",
                new OnlineStatusPayload { UserId = userId });
        }
        await base.OnDisconnectedAsync(exception);
    }

    // ---------- Вспомогательные ----------

    private Task Respond(string eventName, object payload) =>
        Clients.Caller.SendAsync(eventName, payload);

    private async Task NotifyOnlineStatus(int userId)
    {
        foreach (var contactId in _db.GetContactIds(userId))
        {
            if (_registry.IsOnline(contactId))
                await Respond("UserOnline", new OnlineStatusPayload { UserId = contactId });
        }
        await Clients.AllExcept(Context.ConnectionId).SendAsync("UserOnline",
            new OnlineStatusPayload { UserId = userId });
    }

    private async Task PushPendingMessages(int userId)
    {
        var pending = _db.GetPendingMessages(userId);
        if (pending.Count == 0) return;

        _log.Information("Pushing {Count} pending messages to userId={UserId}", pending.Count, userId);
        foreach (var hm in pending)
        {
            using (LogContext.PushProperty("MessageId", hm.MessageId))
            {
                try
                {
                    await Respond("IncomingMessage", new IncomingMessagePayload
                    {
                        SenderId = hm.SenderId,
                        MessageId = hm.MessageId,
                        EncryptedKey = hm.EncryptedKey,
                        EncryptedData = new EncryptedData
                        {
                            Nonce = hm.Nonce ?? "",
                            Ciphertext = hm.Ciphertext ?? "",
                            Tag = hm.Tag ?? ""
                        },
                        ContentType = hm.ContentType,
                        FileName = hm.FileName,
                        FileSize = hm.FileSize,
                        FileTransferId = hm.FileTransferId
                    });
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Failed to deliver pending message messageId={MessageId}", hm.MessageId);
                    return;
                }
            }
        }
    }
}
