using Microsoft.AspNetCore.SignalR;
using SecureChat.Core;
using SecureChat.Security;
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

    private string? Role() => UserId is int uid ? _db.GetUserRole(uid) : null;

    private bool IsAdmin() => Role() == "Admin";

    private string ClientIp =>
        Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private void WriteAudit(int? actorId, string action, int? targetId, string result) =>
        _db.WriteAudit(actorId, action, targetId, result, ClientIp);

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
            _log.Warning("Email code rate limit for {MaskedEmail}", PiiMasker.Email(p.Email));
            await Respond("SendEmailCodeResponse",
                new SendEmailCodeResponsePayload { Status = "RateLimit", Message = "Слишком много запросов. Попробуйте через 15 минут." });
            return;
        }

        string code = _email.GenerateVerificationCode();
        _db.SaveVerificationCode(p.Email, code, TimeSpan.FromMinutes(10));

        bool sent = await _email.SendVerificationCodeAsync(p.Email, code);
        if (sent)
        {
            _log.Information("Verification code sent to {MaskedEmail}", PiiMasker.Email(p.Email));
            await Respond("SendEmailCodeResponse",
                new SendEmailCodeResponsePayload { Status = "Ok", Message = "Код подтверждения отправлен на почту." });
        }
        else
        {
            _log.Error("Failed to send email to {MaskedEmail}", PiiMasker.Email(p.Email));
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

        _log.Information("Login attempt for {MaskedEmail}", PiiMasker.Email(p.Email));

        string ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_db.IsLoginBlocked(p.Email, ip, TimeSpan.FromMinutes(15), maxAttempts: 5))
        {
            _log.Warning("Login rate limit exceeded for {MaskedEmail} ip={Ip}", PiiMasker.Email(p.Email), ip);
            await Respond("LoginResponse", new LoginResponsePayload { Status = "RateLimited" });
            return;
        }

        var auth = _db.Authenticate(p.Email, p.Password);
        if (auth == null)
        {
            _db.RecordFailedLogin(p.Email, ip);
            WriteAudit(null, "FailedLogin", null, "InvalidCredentials");
            _log.Warning("Invalid credentials for {MaskedEmail}", PiiMasker.Email(p.Email));
            await Respond("LoginResponse", new LoginResponsePayload { Status = "InvalidCredentials" });
            return;
        }

        int userId = auth.Value.id;

        if (!_db.IsUserActive(userId))
        {
            WriteAudit(userId, "Login", userId, "Banned");
            _log.Warning("Login rejected: banned userId={UserId}", userId);
            await Respond("LoginResponse", new LoginResponsePayload { Status = "Banned" });
            return;
        }

        _registry.Register(userId, Context.ConnectionId);
        RememberSessionToken(auth.Value.token);

        WriteAudit(userId, "Login", userId, "Ok");
        _log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} success=true", "Login", userId);
        _log.Information("Login successful userId={UserId}", userId);

        await Respond("LoginResponse",
            new LoginResponsePayload
            {
                Status = "Ok", UserId = userId, UserName = auth.Value.name,
                Token = auth.Value.token, Role = _db.GetUserRole(userId)
            });

        await NotifyOnlineStatus(userId);
        await PushPendingMessages(userId);
        await JoinChannelGroups(userId);
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

        _log.Information("Registration request for {MaskedEmail}", PiiMasker.Email(p.Email));

        var verifyResult = _db.VerifyAndConsumeCode(p.Email, p.Code);
        if (verifyResult == Database.CodeVerifyResult.Invalid)
        {
            _log.Warning("Invalid verification code for {MaskedEmail}", PiiMasker.Email(p.Email));
            await Respond("RegisterAccountResponse", new RegisterAccountResponsePayload { Status = "InvalidCode" });
            return;
        }
        if (verifyResult == Database.CodeVerifyResult.Expired)
        {
            _log.Warning("Verification code expired for {MaskedEmail}", PiiMasker.Email(p.Email));
            await Respond("RegisterAccountResponse", new RegisterAccountResponsePayload { Status = "CodeExpired" });
            return;
        }

        var reg = _db.RegisterUser(p.Email, p.Password, p.UserName, p.PublicKey);
        if (reg == null)
        {
            _log.Warning("User already exists: {MaskedEmail}", PiiMasker.Email(p.Email));
            await Respond("RegisterAccountResponse", new RegisterAccountResponsePayload { Status = "UserExists" });
            return;
        }

        int userId = reg.Value.id;
        _registry.Register(userId, Context.ConnectionId);
        RememberSessionToken(reg.Value.token);

        _log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} success=true", "Register", userId);
        _log.Information("New user registered userId={UserId}", userId);

        WriteAudit(userId, "Register", userId, "Ok");

        await Respond("RegisterAccountResponse",
            new RegisterAccountResponsePayload
            {
                Status = "Ok", AssignedId = userId, Token = reg.Value.token, Role = _db.GetUserRole(userId)
            });

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
            new HelloResponsePayload
            {
                Status = "Ok", UserId = userId.Value, UserName = name, Role = _db.GetUserRole(userId.Value)
            });

        await NotifyOnlineStatus(userId.Value);
        await PushPendingMessages(userId.Value);
        await JoinChannelGroups(userId.Value);
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

    // ---------- Администрирование (только Admin, таблица 1.1.3) ----------

    public async Task<SimpleStatusPayload> BanUser(BanUserPayload? p)
    {
        if (!IsAdmin()) return new SimpleStatusPayload { Status = "Forbidden" };
        if (p == null || p.TargetId <= 0 || !_db.UserExists(p.TargetId))
            return new SimpleStatusPayload { Status = "NotFound" };
        if (p.TargetId == UserId)
            return new SimpleStatusPayload { Status = "CannotBanSelf" };
        if (p.Reason.Length > 200 || Validators.HasControlCharacters(p.Reason))
            return new SimpleStatusPayload { Status = "InvalidReason" };

        int adminId = UserId!.Value;
        _db.SetUserActive(p.TargetId, false);
        _db.RevokeAllUserSessions(p.TargetId); // требование 1.1.5: отзыв сессий при бане

        foreach (var connId in _registry.GetConnections(p.TargetId))
        {
            // заблокированный клиент получит событие и отключится сам
            await Clients.Client(connId).SendAsync("Banned", new BannedPayload { Reason = p.Reason });
        }

        // требование 1.1.5/1.2.7: при бане — удаление из каналов;
        // Owner каждого канала выполнит ротацию GroupKey по событию ChannelMemberRemoved
        foreach (var channelId in _db.DeleteUserFromAllChannels(p.TargetId))
        {
            foreach (var connId in _registry.GetConnections(p.TargetId))
                await Groups.RemoveFromGroupAsync(connId, channelId);
            await Clients.Group(channelId).SendAsync("ChannelMemberRemoved",
                new ChannelMemberRemovedPayload { ChannelId = channelId, UserId = p.TargetId });
        }

        WriteAudit(adminId, "BanUser", p.TargetId, "Ok");
        _log.ForContext("Audit", true).Information("AUDIT {Action} adminId={AdminId} targetId={TargetId}",
            "BanUser", adminId, p.TargetId);

        return new SimpleStatusPayload { Status = "Ok" };
    }

    public Task<SimpleStatusPayload> UnbanUser(UnbanUserPayload? p)
    {
        if (!IsAdmin()) return Task.FromResult(new SimpleStatusPayload { Status = "Forbidden" });
        if (p == null || p.TargetId <= 0 || !_db.UserExists(p.TargetId))
            return Task.FromResult(new SimpleStatusPayload { Status = "NotFound" });

        int adminId = UserId!.Value;
        _db.SetUserActive(p.TargetId, true);

        WriteAudit(adminId, "UnbanUser", p.TargetId, "Ok");
        _log.ForContext("Audit", true).Information("AUDIT {Action} adminId={AdminId} targetId={TargetId}",
            "UnbanUser", adminId, p.TargetId);

        return Task.FromResult(new SimpleStatusPayload { Status = "Ok" });
    }

    public Task<SimpleStatusPayload> ChangeRole(ChangeRolePayload? p)
    {
        if (!IsAdmin()) return Task.FromResult(new SimpleStatusPayload { Status = "Forbidden" });
        if (p == null || p.TargetId <= 0 || !_db.UserExists(p.TargetId))
            return Task.FromResult(new SimpleStatusPayload { Status = "NotFound" });
        if (p.TargetId == UserId)
            // защита от потери последнего Admin
            return Task.FromResult(new SimpleStatusPayload { Status = "CannotChangeOwnRole" });
        if (p.Role is not ("Admin" or "HR" or "Employee"))
            return Task.FromResult(new SimpleStatusPayload { Status = "InvalidRole" });

        int adminId = UserId!.Value;
        _db.SetUserRole(p.TargetId, p.Role);

        WriteAudit(adminId, "ChangeRole", p.TargetId, "Ok");
        _log.ForContext("Audit", true).Information("AUDIT {Action} adminId={AdminId} targetId={TargetId} role={Role}",
            "ChangeRole", adminId, p.TargetId, p.Role);

        return Task.FromResult(new SimpleStatusPayload { Status = "Ok" });
    }

    /// <summary>Смена пароля: проверка текущего, отзыв всех сессий (требование 1.1.4).</summary>
    public async Task<SimpleStatusPayload> ChangePassword(ChangePasswordPayload? p)
    {
        if (UserId is not int uid) return new SimpleStatusPayload { Status = "Forbidden" };
        if (p == null) return new SimpleStatusPayload { Status = "Error" };

        if (!_db.VerifyPasswordForUser(uid, p.CurrentPassword, out _))
        {
            WriteAudit(uid, "ChangePassword", uid, "WrongPassword");
            return new SimpleStatusPayload { Status = "WrongPassword" };
        }
        if (!Validators.IsValidPassword(p.NewPassword))
            return new SimpleStatusPayload { Status = "WeakPassword" };

        _db.UpdatePassword(uid, p.NewPassword);
        _db.RevokeAllUserSessions(uid);

        WriteAudit(uid, "ChangePassword", uid, "Ok");
        _log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId}", "ChangePassword", uid);
        _log.Information("Password changed, all sessions revoked userId={UserId}", uid);

        return new SimpleStatusPayload { Status = "Ok" };
    }

    // ---------- Профиль: статус и аватар (требование 1.1.4) ----------

    public Task<SimpleStatusPayload> UpdateStatus(UpdateStatusPayload? p)
    {
        if (UserId is not int uid) return Task.FromResult(new SimpleStatusPayload { Status = "Forbidden" });
        if (p == null || p.Status.Length > 100 || Validators.HasControlCharacters(p.Status))
            return Task.FromResult(new SimpleStatusPayload { Status = "InvalidStatus" });

        _db.SetUserStatus(uid, p.Status);
        _log.Information("Status updated userId={UserId} length={Length}", uid, p.Status.Length);
        return Task.FromResult(new SimpleStatusPayload { Status = "Ok" });
    }

    public async Task<SimpleStatusPayload> UploadAvatar(UploadAvatarPayload? p)
    {
        if (UserId is not int uid) return new SimpleStatusPayload { Status = "Forbidden" };
        if (p == null || string.IsNullOrEmpty(p.DataBase64))
            return new SimpleStatusPayload { Status = "InvalidAvatar" };
        if (p.DataBase64.Length > 2_800_000) // 2 МБ после декодирования
            return new SimpleStatusPayload { Status = "AvatarTooLarge" };

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(p.DataBase64);
        }
        catch (FormatException)
        {
            return new SimpleStatusPayload { Status = "InvalidAvatar" };
        }
        if (bytes.Length > 2 * 1024 * 1024)
            return new SimpleStatusPayload { Status = "AvatarTooLarge" };

        // клиент перекодирует в PNG; сервер проверяет магические байты
        byte[] pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (bytes.Length < pngSignature.Length || !bytes.AsSpan(0, pngSignature.Length).SequenceEqual(pngSignature))
            return new SimpleStatusPayload { Status = "InvalidAvatarFormat" };

        string avatarsDir = Path.Combine(Directory.GetCurrentDirectory(), "avatars");
        Directory.CreateDirectory(avatarsDir);
        string filePath = Path.Combine(avatarsDir, $"{uid}.png");
        await File.WriteAllBytesAsync(filePath, bytes);
        _db.SetAvatarPath(uid, filePath);

        _log.Information("Avatar updated userId={UserId} size={Size}B", uid, bytes.Length);
        return new SimpleStatusPayload { Status = "Ok" };
    }

    /// <summary>Профиль доступен себе и контактам (данные публичны только для контактов).</summary>
    public Task<UserProfileDto?> GetUserProfile(GetUserProfilePayload? p)
    {
        if (UserId is not int uid || p == null || p.TargetId <= 0)
            return Task.FromResult<UserProfileDto?>(null);
        if (p.TargetId != uid && !_db.GetContactIds(uid).Contains(p.TargetId))
            return Task.FromResult<UserProfileDto?>(null);

        string? avatarPath = _db.GetAvatarPath(p.TargetId);
        string? avatarBase64 = null;
        if (avatarPath != null && File.Exists(avatarPath))
        {
            byte[] bytes = File.ReadAllBytes(avatarPath);
            avatarBase64 = Convert.ToBase64String(bytes);
        }

        return Task.FromResult<UserProfileDto?>(new UserProfileDto
        {
            UserId = p.TargetId,
            UserName = _db.GetUserName(p.TargetId) ?? $"User {p.TargetId}",
            Status = _db.GetUserStatus(p.TargetId),
            Role = _db.GetUserRole(p.TargetId),
            IsOnline = _registry.IsOnline(p.TargetId),
            AvatarBase64 = avatarBase64
        });
    }

    // ---------- Аудит (просмотр только Admin, требование 1.1.3) ----------

    public Task<AuditLogResponsePayload?> GetAuditLog(GetAuditLogPayload? p)
    {
        if (!IsAdmin()) return Task.FromResult<AuditLogResponsePayload?>(null);

        int limit = p is { Limit: > 0 and <= 500 } ? p.Limit : 200;
        int offset = p is { Offset: > 0 } ? p.Offset : 0;

        return Task.FromResult<AuditLogResponsePayload?>(new AuditLogResponsePayload
        {
            TotalCount = _db.GetAuditLogCount(),
            Entries = _db.GetAuditLog(limit, offset)
        });
    }

    // ---------- Каналы (Фаза C) ----------

    private bool IsHrOrAdmin() => Role() is "Admin" or "HR";

    /// <summary>Подключение добавляется в SignalR-группы всех каналов пользователя.</summary>
    private async Task JoinChannelGroups(int userId)
    {
        foreach (var channelId in _db.GetUserChannelIds(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, channelId);
    }

    public async Task<CreateChannelResponsePayload> CreateChannel(CreateChannelPayload? p)
    {
        if (UserId is not int uid) return new CreateChannelResponsePayload { Status = "Forbidden" };
        if (!IsHrOrAdmin()) return new CreateChannelResponsePayload { Status = "Forbidden" };
        if (p == null || string.IsNullOrWhiteSpace(p.Name) ||
            p.Name.Length > 100 || Validators.HasControlCharacters(p.Name))
            return new CreateChannelResponsePayload { Status = "InvalidName" };

        string? channelId = _db.CreateChannel(p.Name.Trim(), uid);
        if (channelId == null) return new CreateChannelResponsePayload { Status = "NameExists" };

        await Groups.AddToGroupAsync(Context.ConnectionId, channelId);
        WriteAudit(uid, "CreateChannel", null, "Ok");
        _log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} channelId={ChannelId}",
            "CreateChannel", uid, channelId);

        return new CreateChannelResponsePayload { Status = "Ok", ChannelId = channelId };
    }

    public Task<ChannelListPayload> GetChannels()
    {
        if (UserId is not int uid) return Task.FromResult(new ChannelListPayload());
        return Task.FromResult(new ChannelListPayload { Channels = _db.GetUserChannels(uid) });
    }

    public Task<ChannelMembersPayload?> GetChannelMembers(JoinChannelPayload? p)
    {
        if (UserId is not int uid || p == null ||
            !_db.ChannelExists(p.ChannelId) || !_db.IsChannelMember(p.ChannelId, uid))
            return Task.FromResult<ChannelMembersPayload?>(null);

        var members = _db.GetChannelMembers(p.ChannelId);
        foreach (var m in members) m.IsOnline = _registry.IsOnline(m.UserId);
        return Task.FromResult<ChannelMembersPayload?>(
            new ChannelMembersPayload { ChannelId = p.ChannelId, Members = members });
    }

    public async Task<SimpleStatusPayload> JoinChannel(JoinChannelPayload? p)
    {
        if (UserId is not int uid) return new SimpleStatusPayload { Status = "Forbidden" };
        if (p == null || !_db.ChannelExists(p.ChannelId) || !_db.IsChannelMember(p.ChannelId, uid))
            return new SimpleStatusPayload { Status = "NotFound" };

        await Groups.AddToGroupAsync(Context.ConnectionId, p.ChannelId);

        // восстановление GroupKey после перезапуска клиента (ключ хранится зашифрованным RSA)
        var key = _db.GetChannelKey(p.ChannelId, uid);
        if (key.HasValue)
        {
            await Respond("GroupKeyDelivery", new GroupKeyDeliveryPayload
            {
                ChannelId = p.ChannelId, Version = key.Value.version, EncryptedKey = key.Value.encryptedKey
            });
        }
        return new SimpleStatusPayload { Status = "Ok" };
    }

    public async Task<SimpleStatusPayload> LeaveChannel(JoinChannelPayload? p)
    {
        if (UserId is not int uid) return new SimpleStatusPayload { Status = "Forbidden" };
        if (p == null || !_db.ChannelExists(p.ChannelId))
            return new SimpleStatusPayload { Status = "NotFound" };

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, p.ChannelId);
        return new SimpleStatusPayload { Status = "Ok" };
    }

    public async Task<SimpleStatusPayload> AddChannelMember(AddChannelMemberPayload? p)
    {
        if (UserId is not int uid) return new SimpleStatusPayload { Status = "Forbidden" };
        if (!IsHrOrAdmin()) return new SimpleStatusPayload { Status = "Forbidden" };
        if (p == null || !_db.ChannelExists(p.ChannelId) || !_db.IsChannelMember(p.ChannelId, uid))
            return new SimpleStatusPayload { Status = "NotFound" };
        if (p.UserId <= 0 || !_db.UserExists(p.UserId))
            return new SimpleStatusPayload { Status = "NotFound" };
        if (_db.IsChannelMember(p.ChannelId, p.UserId))
            return new SimpleStatusPayload { Status = "AlreadyMember" };

        _db.AddChannelMemberToChannel(p.ChannelId, p.UserId);

        var targetConns = _registry.GetConnections(p.UserId);
        foreach (var connId in targetConns)
            await Groups.AddToGroupAsync(connId, p.ChannelId);

        await Clients.Group(p.ChannelId).SendAsync("ChannelMemberAdded",
            new ChannelMemberAddedPayload { ChannelId = p.ChannelId, UserId = p.UserId });

        WriteAudit(uid, "AddChannelMember", p.UserId, "Ok");
        _log.ForContext("Audit", true).Information(
            "AUDIT {Action} userId={UserId} channelId={ChannelId} targetId={TargetId}",
            "AddChannelMember", uid, p.ChannelId, p.UserId);

        return new SimpleStatusPayload { Status = "Ok" };
    }

    public async Task<SimpleStatusPayload> RemoveChannelMember(RemoveChannelMemberPayload? p)
    {
        if (UserId is not int uid) return new SimpleStatusPayload { Status = "Forbidden" };
        if (!IsHrOrAdmin()) return new SimpleStatusPayload { Status = "Forbidden" };
        if (p == null || !_db.ChannelExists(p.ChannelId) || !_db.IsChannelMember(p.ChannelId, uid))
            return new SimpleStatusPayload { Status = "NotFound" };
        if (!_db.IsChannelMember(p.ChannelId, p.UserId))
            return new SimpleStatusPayload { Status = "NotFound" };
        if (p.UserId == _db.GetChannelOwnerId(p.ChannelId))
            return new SimpleStatusPayload { Status = "CannotRemoveOwner" };
        if (p.UserId == uid)
            return new SimpleStatusPayload { Status = "CannotRemoveSelf" };

        _db.RemoveChannelMemberFromChannel(p.ChannelId, p.UserId);

        foreach (var connId in _registry.GetConnections(p.UserId))
            await Groups.RemoveFromGroupAsync(connId, p.ChannelId);

        await Clients.Group(p.ChannelId).SendAsync("ChannelMemberRemoved",
            new ChannelMemberRemovedPayload { ChannelId = p.ChannelId, UserId = p.UserId });

        WriteAudit(uid, "RemoveChannelMember", p.UserId, "Ok");
        _log.ForContext("Audit", true).Information(
            "AUDIT {Action} userId={UserId} channelId={ChannelId} targetId={TargetId}",
            "RemoveChannelMember", uid, p.ChannelId, p.UserId);

        return new SimpleStatusPayload { Status = "Ok" };
    }

    /// <summary>
    /// Доставка GroupKey (зашифрованного RSA получателя) — ретрансляция 1-1 + сохранение
    /// для восстановления после перезапуска. Сервер не видит открытый ключ.
    /// </summary>
    public async Task SendGroupKeyDelivery(GroupKeyDeliveryPayload? p)
    {
        if (UserId is not int uid) return;
        if (p == null || p.TargetId <= 0 || string.IsNullOrEmpty(p.ChannelId) ||
            string.IsNullOrEmpty(p.EncryptedKey) || p.Version < 0) return;
        if (!_db.IsChannelMember(p.ChannelId, uid) || !_db.IsChannelMember(p.ChannelId, p.TargetId)) return;

        _db.SaveChannelKey(p.ChannelId, p.TargetId, p.Version, p.EncryptedKey);

        var conns = _registry.GetConnections(p.TargetId);
        if (conns.Count > 0)
        {
            await Clients.Clients(conns).SendAsync("GroupKeyDelivery",
                new GroupKeyDeliveryPayload
                {
                    ChannelId = p.ChannelId, Version = p.Version, EncryptedKey = p.EncryptedKey
                });
        }
        _log.Debug("GroupKey delivered channelId={ChannelId} targetId={TargetId} version={Version}",
            p.ChannelId, p.TargetId, p.Version);
    }

    public async Task SendChannelKeyRotated(ChannelKeyRotatedPayload? p)
    {
        if (UserId is not int uid) return;
        if (p == null || !_db.IsChannelMember(p.ChannelId, uid)) return;
        // анонсировать ротацию может только Owner или HR/Admin
        if (!IsHrOrAdmin() && _db.GetChannelOwnerId(p.ChannelId) != uid) return;

        await Clients.OthersInGroup(p.ChannelId).SendAsync("ChannelKeyRotated", p);
        WriteAudit(uid, "RotateGroupKey", null, "Ok");
        _log.ForContext("Audit", true).Information(
            "AUDIT {Action} userId={UserId} channelId={ChannelId} version={Version}",
            "RotateGroupKey", uid, p.ChannelId, p.Version);
    }

    // ---------- Direct-чаты ----------

    public async Task SendMessage(SendMessagePayload? p)
    {
        int? senderId = UserId;
        if (senderId == null || p == null) return;

        if (p.ReceiverId <= 0 || string.IsNullOrEmpty(p.MessageId) || p.EncryptedData == null)
        {
            await Respond("Error", new SimpleStatusPayload { Status = "MessageRejected" });
            return;
        }

        // лимиты шифртекста (base64): Text — до 4000 символов UTF-8; File — до 10 МБ до кодирования
        int maxCipherBase64 = p.ContentType == "File" ? 14_000_000 : 16_500;
        if (string.IsNullOrEmpty(p.EncryptedData.Ciphertext) || p.EncryptedData.Ciphertext.Length > maxCipherBase64)
        {
            _log.Warning("Message rejected: ciphertext size limit messageId={MessageId} type={ContentType}",
                p.MessageId, p.ContentType);
            await Respond("Error", new SimpleStatusPayload { Status = "MessageRejected" });
            return;
        }

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
        if (userId == null || p == null || p.ContactId <= 0) return;
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
        if (p == null || p.TargetId <= 0) return;
        string? key = _db.GetPublicKey(p.TargetId);
        await Respond("PublicKeyResponse",
            new PublicKeyResponsePayload { Status = key != null ? "Ok" : "NotFound", TargetId = p.TargetId, PublicKey = key ?? "" });
    }

    public async Task GetUserInfo(GetUserInfoPayload? p)
    {
        if (p == null || p.TargetId <= 0) return;
        string? name = _db.GetUserName(p.TargetId);
        await Respond("UserInfoResponse",
            new UserInfoResponsePayload { Status = name != null ? "Ok" : "NotFound", UserId = p.TargetId, UserName = name ?? "" });
    }

    public async Task AddContact(AddContactPayload? p)
    {
        int? userId = UserId;
        if (userId == null || p == null || p.ContactId <= 0) return;

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

        if (!Validators.IsValidUserName(p.UserName))
        {
            _log.Warning("Profile update rejected: invalid userName userId={UserId}", userId.Value);
            await Respond("Error", new SimpleStatusPayload { Status = "InvalidUserName" });
            return;
        }

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
            WriteAudit(userId, "Logout", userId, "Ok");
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
