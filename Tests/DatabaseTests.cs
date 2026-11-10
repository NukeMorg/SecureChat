using Microsoft.Data.Sqlite;
using SecureChat.Core;
using SecureChat.Server;
using Xunit;

namespace SecureChat.Tests;

/// <summary>
/// Тесты серверной БД (SQLite, temp-папка через переключение CWD).
/// Требуют последовательного выполнения — см. ServerDatabaseCollection.
/// </summary>
[Collection(nameof(ServerDatabaseCollection))]
public class DatabaseTests : IDisposable
{
    private readonly TempDbDirectory _tempDir;
    private readonly Database _db;

    public DatabaseTests()
    {
        _tempDir = new TempDbDirectory();
        _db = new Database(_tempDir.ConnectionString);
        _db.Initialize();
    }

    public void Dispose() => _tempDir.Dispose();

    private (int id, string token) RegisterTestUser(string email, string password = "Passw0rd!x", string name = "User") =>
        _db.RegisterUser(email, password, name, "-----BEGIN PUBLIC KEY-----TEST-----END PUBLIC KEY-----").GetValueOrDefault();

    // ---------- Схема и инициализация ----------

    [Fact]
    public void Initialize_CreatesSchemaAndRunsMigrations()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();

        long version = (long)new SqliteCommand("PRAGMA user_version;", conn).ExecuteScalar()!;
        Assert.Equal(3, version);

        var tables = new HashSet<string>();
        using (var cmd = new SqliteCommand("SELECT name FROM sqlite_master WHERE type='table';", conn))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) tables.Add(reader.GetString(0));

        foreach (string expected in new[] { "Clients", "Sessions", "EmailVerifications", "EmailRateLimits",
                 "LoginAttempts", "Messages", "Contacts", "Channels", "ChannelMembers", "ChannelKeys", "AuditLog" })
        {
            Assert.Contains(expected, tables);
        }
    }

    // ---------- Регистрация и аутентификация ----------

    [Fact]
    public void RegisterUser_AssignsSequentialIdAndToken()
    {
        var user1 = _db.RegisterUser("reg1@test.local", "Passw0rd!x", "Alice", "PUBKEY1");
        var user2 = _db.RegisterUser("reg2@test.local", "Passw0rd!x", "Bob", "PUBKEY2");

        Assert.NotNull(user1);
        Assert.NotNull(user2);
        Assert.NotEqual(user1!.Value.id, user2!.Value.id);
        Assert.False(string.IsNullOrEmpty(user1.Value.token));
        Assert.NotEqual(user1.Value.token, user2.Value.token);
    }

    [Fact]
    public void RegisterUser_DuplicateEmail_ReturnsNull()
    {
        Assert.NotNull(_db.RegisterUser("dup@test.local", "Passw0rd!x", "Alice", "PK"));
        Assert.Null(_db.RegisterUser("dup@test.local", "Passw0rd!y", "Bob", "PK2"));
    }

    [Fact]
    public void Authenticate_CorrectCredentials_ReturnsUserAndNewToken()
    {
        var registered = RegisterTestUser("auth@test.local", "Passw0rd!x", "Alice");

        var auth = _db.Authenticate("auth@test.local", "Passw0rd!x");

        Assert.NotNull(auth);
        Assert.Equal(registered.id, auth!.Value.id);
        Assert.Equal("Alice", auth.Value.name);
        Assert.False(string.IsNullOrEmpty(auth.Value.token));
    }

    [Fact]
    public void Authenticate_WrongPassword_ReturnsNull()
    {
        RegisterTestUser("wrongpw@test.local");
        Assert.Null(_db.Authenticate("wrongpw@test.local", "WrongPassword9"));
    }

    [Fact]
    public void Authenticate_UnknownEmail_ReturnsNull() =>
        Assert.Null(_db.Authenticate("ghost@test.local", "Passw0rd!x"));

    [Fact]
    public void Authenticate_EmailCaseAndWhitespaceInsensitive()
    {
        RegisterTestUser("Case@Test.LOCAL");
        Assert.NotNull(_db.Authenticate("  case@test.local  ", "Passw0rd!x"));
    }

    [Fact]
    public void RegisterUser_DefaultRoleIsEmployee() =>
        Assert.Equal("Employee", _db.GetUserRole(RegisterTestUser("role@test.local").id));

    // ---------- Сессии ----------

    [Fact]
    public void CreateSession_ValidateSession_RoundTrip()
    {
        int userId = RegisterTestUser("sess@test.local").id;
        string token = _db.CreateSession(userId);

        Assert.Equal(userId, _db.ValidateSession(token));
    }

    [Fact]
    public void ValidateSession_UnknownToken_ReturnsNull() =>
        Assert.Null(_db.ValidateSession("no-such-token"));

    [Fact]
    public void RevokeSession_TokenBecomesInvalid()
    {
        int userId = RegisterTestUser("revoke@test.local").id;
        string token = _db.CreateSession(userId);

        _db.RevokeSession(token);

        Assert.Null(_db.ValidateSession(token));
    }

    [Fact]
    public void RevokeAllUserSessions_AllTokensInvalid()
    {
        int userId = RegisterTestUser("revokeall@test.local").id;
        string token1 = _db.CreateSession(userId);
        string token2 = _db.CreateSession(userId);

        _db.RevokeAllUserSessions(userId);

        Assert.Null(_db.ValidateSession(token1));
        Assert.Null(_db.ValidateSession(token2));
    }

    // ---------- Коды подтверждения email ----------

    [Fact]
    public void VerifyAndConsumeCode_ValidCode_ConsumedOnce()
    {
        _db.SaveVerificationCode("code@test.local", "123456", TimeSpan.FromMinutes(10));

        Assert.Equal(CodeVerifyResult.Valid, _db.VerifyAndConsumeCode("code@test.local", "123456"));
        Assert.Equal(CodeVerifyResult.Invalid, _db.VerifyAndConsumeCode("code@test.local", "123456"));
    }

    [Fact]
    public void VerifyAndConsumeCode_WrongCode_ReturnsInvalid()
    {
        _db.SaveVerificationCode("wrong@test.local", "111111", TimeSpan.FromMinutes(10));
        Assert.Equal(CodeVerifyResult.Invalid, _db.VerifyAndConsumeCode("wrong@test.local", "222222"));
    }

    [Fact]
    public void VerifyAndConsumeCode_UnknownEmail_ReturnsInvalid() =>
        Assert.Equal(CodeVerifyResult.Invalid, _db.VerifyAndConsumeCode("nocode@test.local", "123456"));

    [Fact]
    public void VerifyAndConsumeCode_ExpiredTtl_ReturnsExpired()
    {
        _db.SaveVerificationCode("expired@test.local", "333333", TimeSpan.FromMinutes(-1));
        Assert.Equal(CodeVerifyResult.Expired, _db.VerifyAndConsumeCode("expired@test.local", "333333"));
    }

    [Fact]
    public void VerifyAndConsumeCode_EmailNormalization_TrimsAndLowercases()
    {
        _db.SaveVerificationCode("  Norm@Test.Local  ", "444444", TimeSpan.FromMinutes(10));
        Assert.Equal(CodeVerifyResult.Valid, _db.VerifyAndConsumeCode("norm@test.local", " 444444 "));
    }

    // ---------- Брутфорс и лимит писем ----------

    [Fact]
    public void IsLoginBlocked_BelowMaxAttempts_ReturnsFalse()
    {
        string email = "brute1@test.local";
        for (int i = 0; i < 4; i++) _db.RecordFailedLogin(email, "10.0.0.1");

        Assert.False(_db.IsLoginBlocked(email, "10.0.0.1", TimeSpan.FromMinutes(15), maxAttempts: 5));
    }

    [Fact]
    public void IsLoginBlocked_AtMaxAttempts_ReturnsTrue()
    {
        string email = "brute2@test.local";
        for (int i = 0; i < 5; i++) _db.RecordFailedLogin(email, "10.0.0.2");

        Assert.True(_db.IsLoginBlocked(email, "10.0.0.2", TimeSpan.FromMinutes(15), maxAttempts: 5));
    }

    [Fact]
    public void IsLoginBlocked_BlocksByIpRegardlessOfEmail()
    {
        for (int i = 0; i < 5; i++) _db.RecordFailedLogin($"victim{i}@test.local", "10.0.0.3");

        // та же IP, другой email — тоже заблокирован (условие Email OR IpAddress)
        Assert.True(_db.IsLoginBlocked("other@test.local", "10.0.0.3", TimeSpan.FromMinutes(15), maxAttempts: 5));
        // другая IP и другой email — не заблокирован
        Assert.False(_db.IsLoginBlocked("other@test.local", "10.0.0.99", TimeSpan.FromMinutes(15), maxAttempts: 5));
    }

    [Fact]
    public void TryReserveEmailSend_LimitsWithinWindow()
    {
        string email = "ratelimit@test.local";

        Assert.True(_db.TryReserveEmailSend(email, TimeSpan.FromMinutes(10), maxAttempts: 3));
        Assert.True(_db.TryReserveEmailSend(email, TimeSpan.FromMinutes(10), maxAttempts: 3));
        Assert.True(_db.TryReserveEmailSend(email, TimeSpan.FromMinutes(10), maxAttempts: 3));
        Assert.False(_db.TryReserveEmailSend(email, TimeSpan.FromMinutes(10), maxAttempts: 3));
    }

    [Fact]
    public void TryReserveEmailSend_PerEmailIndependent()
    {
        Assert.True(_db.TryReserveEmailSend("rateA@test.local", TimeSpan.FromMinutes(10), maxAttempts: 1));
        Assert.False(_db.TryReserveEmailSend("rateA@test.local", TimeSpan.FromMinutes(10), maxAttempts: 1));
        Assert.True(_db.TryReserveEmailSend("rateB@test.local", TimeSpan.FromMinutes(10), maxAttempts: 1));
    }

    // ---------- Профиль: роли, бан, статус, аватар, пароль ----------

    [Fact]
    public void SetUserRole_GetUserRole_RoundTrip()
    {
        int userId = RegisterTestUser("setrole@test.local").id;
        _db.SetUserRole(userId, "Manager");
        Assert.Equal("Manager", _db.GetUserRole(userId));
    }

    [Fact]
    public void SetUserActive_BanAndUnban()
    {
        int userId = RegisterTestUser("ban@test.local").id;

        _db.SetUserActive(userId, false);
        Assert.False(_db.IsUserActive(userId));

        _db.SetUserActive(userId, true);
        Assert.True(_db.IsUserActive(userId));
    }

    [Fact]
    public void SetUserStatus_GetUserStatus_RoundTrip()
    {
        int userId = RegisterTestUser("status@test.local").id;
        _db.SetUserStatus(userId, "В отпуске");
        Assert.Equal("В отпуске", _db.GetUserStatus(userId));
    }

    [Fact]
    public void SetAvatarPath_GetAvatarPath_RoundTrip()
    {
        int userId = RegisterTestUser("avatar@test.local").id;
        Assert.Null(_db.GetAvatarPath(userId));

        _db.SetAvatarPath(userId, "avatars/42.png");
        Assert.Equal("avatars/42.png", _db.GetAvatarPath(userId));

        _db.SetAvatarPath(userId, null);
        Assert.Null(_db.GetAvatarPath(userId));
    }

    [Fact]
    public void VerifyPasswordForUser_CorrectPassword_ReturnsTrueWithoutUpgrade()
    {
        var registered = _db.RegisterUser("verify@test.local", "Passw0rd!x", "Alice", "PK");
        Assert.NotNull(registered);

        bool ok = _db.VerifyPasswordForUser(registered!.Value.id, "Passw0rd!x", out string? upgradeHash);

        Assert.True(ok);
        Assert.Null(upgradeHash); // BCrypt-хэш не требует апгрейда
    }

    [Fact]
    public void VerifyPasswordForUser_WrongPassword_ReturnsFalse() =>
        Assert.False(_db.VerifyPasswordForUser(RegisterTestUser("verify2@test.local").id, "bad", out _));

    [Fact]
    public void UpdatePassword_NewPasswordAuthenticates()
    {
        int userId = RegisterTestUser("updpass@test.local").id;
        _db.UpdatePassword(userId, "NewPassw0rd!9");

        Assert.Null(_db.Authenticate("updpass@test.local", "Passw0rd!x"));
        Assert.NotNull(_db.Authenticate("updpass@test.local", "NewPassw0rd!9"));
    }

    [Fact]
    public void UserExists_And_PublicKey()
    {
        var user = RegisterTestUser("exists@test.local");
        Assert.True(_db.UserExists(user.id));
        Assert.False(_db.UserExists(999999));
        Assert.NotNull(_db.GetPublicKey(user.id));
        Assert.Null(_db.GetPublicKey(999999));
    }

    [Fact]
    public void UpdateUserName_GetUserName_RoundTrip()
    {
        int userId = RegisterTestUser("username@test.local", name: "Original").id;
        _db.UpdateUserName(userId, "Renamed");
        Assert.Equal("Renamed", _db.GetUserName(userId));
    }

    // ---------- Контакты ----------

    [Fact]
    public void AddContact_CreatesBidirectionalLinkWithoutDuplicates()
    {
        int alice = RegisterTestUser("alice@test.local").id;
        int bob = RegisterTestUser("bob@test.local").id;

        _db.AddContact(alice, bob);
        _db.AddContact(alice, bob); // повторная попытка не должна дублировать

        Assert.Equal([bob], _db.GetContactIds(alice));
        Assert.Equal([alice], _db.GetContactIds(bob));
    }

    // ---------- Прямые сообщения ----------

    private void SaveTextMessage(string messageId, int senderId, int receiverId, string status = "Sent") =>
        _db.SaveMessage(messageId, senderId, receiverId,
            encryptedKey: "encKeyBase64", nonce: "nonceBase64", ciphertext: "ctBase64", tag: "tagBase64",
            contentType: "Text", fileName: null, fileSize: 0, fileTransferId: null, status: status);

    [Fact]
    public void GetMessageInfo_ReturnsRoutingMetadata()
    {
        int alice = RegisterTestUser("msgmeta@test.local").id;
        int bob = RegisterTestUser("msgmeta2@test.local").id;
        SaveTextMessage("m-meta-1", alice, bob);

        var info = _db.GetMessageInfo("m-meta-1");

        Assert.NotNull(info);
        Assert.Equal(alice, info!.Value.senderId);
        Assert.Equal(bob, info.Value.receiverId);
        Assert.Null(info.Value.channelId);
        Assert.False(string.IsNullOrEmpty(info.Value.timestamp));
    }

    [Fact]
    public void GetMessageInfo_UnknownMessage_ReturnsNull() =>
        Assert.Null(_db.GetMessageInfo("no-such-message"));

    [Fact]
    public void UpdateMessageStatus_ChangesStatus()
    {
        int alice = RegisterTestUser("msgstatus@test.local").id;
        int bob = RegisterTestUser("msgstatus2@test.local").id;
        SaveTextMessage("m-status-1", alice, bob);

        _db.UpdateMessageStatus("m-status-1", "Delivered");

        var history = _db.GetHistoryPage(alice, bob, beforeTimestamp: null, limit: 50);
        Assert.All(history.messages.Where(m => m.MessageId == "m-status-1"), m => Assert.Equal("Delivered", m.Status));
    }

    [Fact]
    public void UpdateMessageContent_MarksMessageEdited()
    {
        int alice = RegisterTestUser("msgedit@test.local").id;
        int bob = RegisterTestUser("msgedit2@test.local").id;
        SaveTextMessage("m-edit-1", alice, bob);

        _db.UpdateMessageContent("m-edit-1", "newNonce", "newCiphertext", "newTag");

        var (messages, _) = _db.GetHistoryPage(alice, bob, beforeTimestamp: null, limit: 50);
        var edited = messages.Single(m => m.MessageId == "m-edit-1");
        Assert.True(edited.IsEdited);
        Assert.Equal("newCiphertext", edited.Ciphertext);
        Assert.Equal("newNonce", edited.Nonce);
        Assert.Equal("newTag", edited.Tag);
    }

    [Fact]
    public void SoftDeleteMessage_MarksDeletedButKeepsRow()
    {
        int alice = RegisterTestUser("msgdel@test.local").id;
        int bob = RegisterTestUser("msgdel2@test.local").id;
        SaveTextMessage("m-del-1", alice, bob);

        _db.SoftDeleteMessage("m-del-1");

        var (messages, _) = _db.GetHistoryPage(alice, bob, beforeTimestamp: null, limit: 50);
        var deleted = messages.Single(m => m.MessageId == "m-del-1");
        Assert.True(deleted.IsDeleted);
        Assert.NotNull(_db.GetMessageInfo("m-del-1")); // строка сохранена (soft-delete)
    }

    [Fact]
    public void GetHistoryPage_ReturnsMessagesInChronologicalOrder()
    {
        int alice = RegisterTestUser("hist@test.local").id;
        int bob = RegisterTestUser("hist2@test.local").id;

        for (int i = 1; i <= 5; i++)
        {
            SaveTextMessage($"m-hist-{i}", alice, bob);
            Thread.Sleep(5); // гарантированно различные Timestamp для порядка
        }

        var (messages, hasMore) = _db.GetHistoryPage(alice, bob, beforeTimestamp: null, limit: 50);

        Assert.False(hasMore);
        Assert.Equal(5, messages.Count);
        Assert.Equal(new[] { "m-hist-1", "m-hist-2", "m-hist-3", "m-hist-4", "m-hist-5" },
            messages.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void GetHistoryPage_PagingWithBeforeCursorAndHasMore()
    {
        int alice = RegisterTestUser("page@test.local").id;
        int bob = RegisterTestUser("page2@test.local").id;

        for (int i = 1; i <= 5; i++)
        {
            SaveTextMessage($"m-page-{i}", alice, bob);
            Thread.Sleep(5);
        }

        var (firstPage, hasMore1) = _db.GetHistoryPage(alice, bob, beforeTimestamp: null, limit: 3);
        Assert.True(hasMore1);
        Assert.Equal(3, firstPage.Count);
        Assert.Equal(new[] { "m-page-3", "m-page-4", "m-page-5" }, firstPage.Select(m => m.MessageId).ToArray());

        var (secondPage, hasMore2) = _db.GetHistoryPage(alice, bob, firstPage[0].Timestamp, limit: 3);
        Assert.False(hasMore2);
        Assert.Equal(new[] { "m-page-1", "m-page-2" }, secondPage.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void GetHistoryPage_DoesNotLeakOtherDialogs()
    {
        int alice = RegisterTestUser("leak@test.local").id;
        int bob = RegisterTestUser("leak2@test.local").id;
        int carol = RegisterTestUser("leak3@test.local").id;

        SaveTextMessage("m-leak-ab", alice, bob);
        SaveTextMessage("m-leak-ac", alice, carol);

        var (messages, _) = _db.GetHistoryPage(alice, bob, beforeTimestamp: null, limit: 50);
        Assert.Equal(["m-leak-ab"], messages.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void GetPendingMessages_ReturnsOnlyUnacknowledgedSentMessages()
    {
        int alice = RegisterTestUser("pending@test.local").id;
        int bob = RegisterTestUser("pending2@test.local").id;

        SaveTextMessage("m-pending-1", alice, bob, status: "Sent");
        SaveTextMessage("m-pending-2", alice, bob, status: "Delivered"); // уже доставлено — не должно попасть
        SaveTextMessage("m-pending-3", bob, alice, status: "Sent");      // чужое для bob — не должно попасть

        var pending = _db.GetPendingMessages(bob);

        Assert.Equal(["m-pending-1"], pending.Select(m => m.MessageId).ToArray());
        Assert.Equal(alice, pending[0].SenderId);
    }

    // ---------- Каналы ----------

    [Fact]
    public void CreateChannel_AssignsIdAndOwnerMembership()
    {
        int ownerId = RegisterTestUser("chowner@test.local").id;

        string? channelId = _db.CreateChannel("Проект Феникс", ownerId);

        Assert.NotNull(channelId);
        Assert.True(_db.ChannelExists(channelId!));
        Assert.Equal("Проект Феникс", _db.GetChannelName(channelId));
        Assert.Equal(ownerId, _db.GetChannelOwnerId(channelId));
        Assert.True(_db.IsChannelMember(channelId!, ownerId));
    }

    [Fact]
    public void CreateChannel_DuplicateName_ReturnsNull()
    {
        int ownerId = RegisterTestUser("chdup@test.local").id;

        string? first = _db.CreateChannel("Уникальный канал", ownerId);
        string? second = _db.CreateChannel("Уникальный канал", ownerId);

        Assert.NotNull(first);
        Assert.Null(second); // UNIQUE(Name)
    }

    [Fact]
    public void ChannelMembers_AddRemoveAndRoles()
    {
        int owner = RegisterTestUser("chm-owner@test.local").id;
        int member = RegisterTestUser("chm-member@test.local").id;
        string channelId = _db.CreateChannel("Канал участников", owner)!;

        _db.AddChannelMemberToChannel(channelId, member);
        Assert.True(_db.IsChannelMember(channelId, member));

        var members = _db.GetChannelMembers(channelId);
        Assert.Equal(2, members.Count);
        Assert.Contains(members, m => m.UserId == owner && m.Role == "Owner" && m.UserName == "User");
        Assert.Contains(members, m => m.UserId == member && m.Role == "Member");

        _db.RemoveChannelMemberFromChannel(channelId, member);
        Assert.False(_db.IsChannelMember(channelId, member));
    }

    [Fact]
    public void GetUserChannels_ReturnsOnlyChannelsUserBelongsTo()
    {
        int owner = RegisterTestUser("guc-owner@test.local").id;
        int outsider = RegisterTestUser("guc-outsider@test.local").id;
        int member = RegisterTestUser("guc-member@test.local").id;
        string channelId = _db.CreateChannel("Список каналов", owner)!;
        _db.AddChannelMemberToChannel(channelId, member);

        var ownerChannels = _db.GetUserChannels(owner);
        Assert.Single(ownerChannels);
        Assert.Equal("Список каналов", ownerChannels[0].Name);
        Assert.Equal(owner, ownerChannels[0].OwnerId);
        Assert.Equal(2, ownerChannels[0].MembersCount); // владелец + добавленный участник

        Assert.Equal([channelId], _db.GetUserChannelIds(member));
        Assert.Empty(_db.GetUserChannels(outsider));
        Assert.Empty(_db.GetUserChannelIds(outsider));
    }

    [Fact]
    public void ChannelKeys_SaveGetAndUpdate()
    {
        int ownerId = RegisterTestUser("ckey@test.local").id;
        string channelId = _db.CreateChannel("Канал ключей", ownerId)!;

        Assert.Null(_db.GetChannelKey(channelId, ownerId));

        _db.SaveChannelKey(channelId, ownerId, version: 1, encryptedKey: "rsaWrappedKeyV1");
        var key = _db.GetChannelKey(channelId, ownerId);
        Assert.Equal((1L, "rsaWrappedKeyV1"), key);

        _db.SaveChannelKey(channelId, ownerId, version: 2, encryptedKey: "rsaWrappedKeyV2");
        Assert.Equal((2L, "rsaWrappedKeyV2"), _db.GetChannelKey(channelId, ownerId));
    }

    [Fact]
    public void ChannelMessages_HistoryPagingAndIsolation()
    {
        int owner = RegisterTestUser("chmsg-owner@test.local").id;
        int member = RegisterTestUser("chmsg-member@test.local").id;
        string channelId = _db.CreateChannel("Канал сообщений", owner)!;
        _db.AddChannelMemberToChannel(channelId, member);

        for (int i = 1; i <= 3; i++)
        {
            _db.SaveChannelMessage($"cm-{i}", channelId, senderId: member,
                nonce: "n", ciphertext: $"c{i}", tag: "t",
                contentType: "Text", fileName: null, fileSize: 0, fileTransferId: null,
                chainIteration: i - 1);
            Thread.Sleep(5);
        }

        var (messages, hasMore) = _db.GetChannelHistoryPage(channelId, beforeTimestamp: null, limit: 50);
        Assert.False(hasMore);
        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.Equal(member, m.SenderId));
        Assert.Equal([2L, 1L, 0L], messages.Select(m => m.ChainIteration).ToArray());

        // постранично
        var (page, hasMore2) = _db.GetChannelHistoryPage(channelId, beforeTimestamp: null, limit: 2);
        Assert.True(hasMore2);
        Assert.Equal(2, page.Count);

        // сообщения канала не попадают в Direct-историю
        var (direct, _) = _db.GetHistoryPage(owner, member, beforeTimestamp: null, limit: 50);
        Assert.Empty(direct);
    }

    [Fact]
    public void DeleteUserFromAllChannels_RemovesMembershipAndListsAffected()
    {
        int owner = RegisterTestUser("dall-owner@test.local").id;
        int member = RegisterTestUser("dall-member@test.local").id;
        string ch1 = _db.CreateChannel("Канал А", owner)!;
        string ch2 = _db.CreateChannel("Канал Б", owner)!;
        _db.AddChannelMemberToChannel(ch1, member);
        _db.AddChannelMemberToChannel(ch2, member);

        var affected = _db.DeleteUserFromAllChannels(member);

        Assert.Equal(new[] { ch1, ch2 }.OrderBy(x => x).ToArray(), affected.OrderBy(x => x).ToArray());
        Assert.False(_db.IsChannelMember(ch1, member));
        Assert.False(_db.IsChannelMember(ch2, member));
        Assert.True(_db.IsChannelMember(ch1, owner)); // владелец не тронут
    }

    // ---------- Аудит ----------

    [Fact]
    public void WriteAudit_GetAuditLog_RoundTrip()
    {
        int userId = RegisterTestUser("audit@test.local").id;

        _db.WriteAudit(userId, "CreateChannel", targetId: null, result: "Ok", ip: "127.0.0.1", userAgent: "test-agent");
        _db.WriteAudit(null, "FailedLogin", targetId: null, result: "InvalidCredentials", ip: "10.1.1.1");

        Assert.True(_db.GetAuditLogCount() >= 2);

        var entries = _db.GetAuditLog(limit: 100, offset: 0);
        var ours = entries.Where(e => e.Action == "CreateChannel").ToList();
        Assert.NotEmpty(ours);
        var entry = ours[^1];
        Assert.Equal(userId, entry.ActorId);
        Assert.Equal("Ok", entry.Result);
        Assert.Equal("127.0.0.1", entry.IpAddress);
        Assert.False(string.IsNullOrEmpty(entry.Timestamp));
    }
}
