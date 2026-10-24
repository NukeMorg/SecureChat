using Microsoft.Extensions.Configuration;
using SecureChat.Common;
using Serilog;
using Serilog.Context;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SecureChat.Server;

class Server
{
    static ConcurrentDictionary<int, WebSocket> clients = new();
    static ConcurrentDictionary<WebSocket, SemaphoreSlim> socketLocks = new();
    static Database db = new();
    static EmailService emailService = null!;

    public static void Initialize(IConfiguration configuration)
    {
        emailService = new EmailService(configuration);
    }

    static async Task Main()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddUserSecrets<Server>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .CreateLogger();

        try
        {
            Log.Information("=== SecureChat Server запуск на {Machine} ===", Environment.MachineName);
            Initialize(configuration);
            db.Initialize();

            var listener = new HttpListener();
            listener.Prefixes.Add("http://*:9000/");
            listener.Prefixes.Add("http://localhost:9000/");
            try
            {
                listener.Start();
                Log.Information("WebSocket сервер запущен на ws://*:9000/");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Не удалось привязать *:9000, фолбэк на localhost");
                listener = new HttpListener();
                listener.Prefixes.Add("http://localhost:9000/");
                listener.Start();
                Log.Information("WebSocket сервер запущен на ws://localhost:9000/");
            }

            while (true)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    if (context.Request.IsWebSocketRequest)
                    {
                        _ = Task.Run(async () =>
                        {
                            HttpListenerWebSocketContext wsContext;
                            try { wsContext = await context.AcceptWebSocketAsync(subProtocol: null); }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "AcceptWebSocketAsync failed");
                                context.Response.StatusCode = 500;
                                context.Response.Close();
                                return;
                            }
                            await HandleClient(wsContext.WebSocket);
                        });
                    }
                    else
                    {
                        byte[] response = Encoding.UTF8.GetBytes("SecureChat WebSocket Server is Running");
                        context.Response.ContentType = "text/plain; charset=utf-8";
                        context.Response.ContentLength64 = response.Length;
                        await context.Response.OutputStream.WriteAsync(response, 0, response.Length);
                        context.Response.Close();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Ошибка HttpListener в главном цикле");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Фатальная ошибка при запуске сервера");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    static async Task HandleClient(WebSocket ws)
    {
        int? clientId = null;
        string connectionId = Guid.NewGuid().ToString("N").Substring(0, 8);
        byte[] buffer = new byte[64 * 1024];
        socketLocks[ws] = new SemaphoreSlim(1, 1);

        using (LogContext.PushProperty("ConnectionId", connectionId))
        {
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    string json = Encoding.UTF8.GetString(ms.ToArray());
                    var msg = JsonSerializer.Deserialize<BaseMessage>(json);
                    if (msg == null) continue;

                    using var clientProp = clientId.HasValue
                        ? LogContext.PushProperty("ClientId", clientId.Value)
                        : null;

                    switch (msg.Type)
                    {
                        case "SendEmailCode": await ProcessSendEmailCode(msg, ws); break;
                        case "Login": clientId = await ProcessLogin(msg, ws); break;
                        case "RegisterAccount": clientId = await ProcessRegisterAccount(msg, ws); break;
                        case "Hello": clientId = await ProcessHello(msg, ws, clientId); break;
                        case "GetPublicKey" when clientId != null: await ProcessGetPublicKey(msg, ws); break;
                        case "GetUserInfo" when clientId != null: await ProcessGetUserInfo(msg, ws); break;
                        case "SendMessage" when clientId != null: await ProcessSendMessage(msg, clientId.Value, ws); break;
                        case "Ack" when clientId != null: await ProcessAck(msg); break;
                        case "GetHistory" when clientId != null: await ProcessGetHistory(msg, clientId.Value, ws); break;
                        case "GetContacts" when clientId != null: await ProcessGetContacts(clientId.Value, ws); break;
                        case "UpdateProfile" when clientId != null: await ProcessUpdateProfile(msg, clientId.Value); break;
                        case "AddContact" when clientId != null: await ProcessAddContact(msg, clientId.Value, ws); break;
                        case "Ping": await Send(ws, new { Type = "Pong" }); break;
                        default:
                            Log.Warning("Неизвестный тип сообщения {Type} clientId={ClientId}", msg.Type, clientId);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Разрыв соединения clientId={ClientId} connectionId={ConnectionId}", clientId, connectionId);
            }
            finally
            {
                if (clientId.HasValue)
                {
                    clients.TryRemove(clientId.Value, out _);
                    Log.ForContext("Audit", true).Information(
                        "AUDIT {Action} userId={UserId} connectionId={ConnectionId}",
                        "Logout", clientId.Value, connectionId);
                    _ = Task.Run(async () => await BroadcastExcept(clientId.Value,
                        new { Type = "UserOffline", Payload = new OnlineStatusPayload { UserId = clientId.Value } }));
                }
                if (socketLocks.TryRemove(ws, out var sem)) sem.Dispose();
                try { ws.Dispose(); } catch { }
            }
        }
    }

    static async Task ProcessSendEmailCode(BaseMessage msg, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<SendEmailCodePayload>();
        if (p == null || string.IsNullOrWhiteSpace(p.Email) || !p.Email.Contains('@'))
        {
            await Send(ws, new { Type = "SendEmailCodeResponse", Payload = new SendEmailCodeResponsePayload { Status = "InvalidEmail", Message = "Некорректный адрес почты." } });
            return;
        }

        if (!db.TryReserveEmailSend(p.Email, TimeSpan.FromMinutes(15), maxAttempts: 3))
        {
            Log.Warning("Rate limit для {MaskedEmail}", MaskEmail(p.Email));
            await Send(ws, new { Type = "SendEmailCodeResponse", Payload = new SendEmailCodeResponsePayload { Status = "RateLimit", Message = "Слишком много запросов. Попробуйте через 15 минут." } });
            return;
        }

        string code = emailService.GenerateVerificationCode();
        db.SaveVerificationCode(p.Email, code, TimeSpan.FromMinutes(10));

        bool sent = await emailService.SendVerificationCodeAsync(p.Email, code);
        if (sent)
        {
            Log.Information("Код подтверждения отправлен на {MaskedEmail}", MaskEmail(p.Email));
            await Send(ws, new { Type = "SendEmailCodeResponse", Payload = new SendEmailCodeResponsePayload { Status = "Ok", Message = "Код подтверждения отправлен на почту." } });
        }
        else
        {
            Log.Error("Не удалось отправить письмо на {MaskedEmail}", MaskEmail(p.Email));
            await Send(ws, new { Type = "SendEmailCodeResponse", Payload = new SendEmailCodeResponsePayload { Status = "SendFailed", Message = "Не удалось отправить письмо." } });
        }
    }

    static async Task<int?> ProcessLogin(BaseMessage msg, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<LoginPayload>();
        if (p == null || string.IsNullOrWhiteSpace(p.Email))
        {
            await Send(ws, new { Type = "LoginResponse", Payload = new LoginResponsePayload { Status = "InvalidCredentials" } });
            return null;
        }

        Log.Information("Попытка входа для {MaskedEmail}", MaskEmail(p.Email));
        var authResult = db.Authenticate(p.Email, p.Password);
        if (authResult == null)
        {
            Log.Warning("Неверные учётные данные для {MaskedEmail}", MaskEmail(p.Email));
            await Send(ws, new { Type = "LoginResponse", Payload = new LoginResponsePayload { Status = "InvalidCredentials" } });
            return null;
        }

        int userId = authResult.Value.id;
        string userName = authResult.Value.name;
        string token = authResult.Value.token;
        clients[userId] = ws;

        Log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} success=true", "Login", userId);
        Log.Information("Успешный вход userId={UserId}", userId);

        await Send(ws, new
        {
            Type = "LoginResponse",
            Payload = new LoginResponsePayload { Status = "Ok", UserId = userId, UserName = userName, Token = token }
        });

        await NotifyOnlineStatus(userId, ws);
        await PushPendingMessages(userId, ws);
        return userId;
    }

    static async Task<int?> ProcessRegisterAccount(BaseMessage msg, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<RegisterAccountPayload>();
        if (p == null || string.IsNullOrWhiteSpace(p.Email))
        {
            await Send(ws, new { Type = "RegisterAccountResponse", Payload = new RegisterAccountResponsePayload { Status = "Error" } });
            return null;
        }

        Log.Information("Запрос на регистрацию {MaskedEmail}", MaskEmail(p.Email));

        var verifyResult = db.VerifyAndConsumeCode(p.Email, p.Code);
        if (verifyResult == Database.CodeVerifyResult.Invalid)
        {
            Log.Warning("Неверный код для {MaskedEmail}", MaskEmail(p.Email));
            await Send(ws, new { Type = "RegisterAccountResponse", Payload = new RegisterAccountResponsePayload { Status = "InvalidCode" } });
            return null;
        }
        if (verifyResult == Database.CodeVerifyResult.Expired)
        {
            Log.Warning("Срок действия кода истёк для {MaskedEmail}", MaskEmail(p.Email));
            await Send(ws, new { Type = "RegisterAccountResponse", Payload = new RegisterAccountResponsePayload { Status = "CodeExpired" } });
            return null;
        }

        var regResult = db.RegisterUser(p.Email, p.Password, p.UserName, p.PublicKey);
        if (regResult == null)
        {
            Log.Warning("Пользователь уже существует: {MaskedEmail}", MaskEmail(p.Email));
            await Send(ws, new { Type = "RegisterAccountResponse", Payload = new RegisterAccountResponsePayload { Status = "UserExists" } });
            return null;
        }

        int userId = regResult.Value.id;
        string token = regResult.Value.token;
        clients[userId] = ws;

        Log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} success=true", "Register", userId);
        Log.Information("Зарегистрирован новый пользователь userId={UserId}", userId);

        await Send(ws, new
        {
            Type = "RegisterAccountResponse",
            Payload = new RegisterAccountResponsePayload { Status = "Ok", AssignedId = userId, Token = token }
        });

        await NotifyOnlineStatus(userId, ws);
        return userId;
    }

    static async Task<int?> ProcessHello(BaseMessage msg, WebSocket ws, int? current)
    {
        var p = msg.Payload.Deserialize<HelloPayload>();
        if (p == null) return null;

        int? userId = null;
        if (!string.IsNullOrEmpty(p.Token)) userId = db.ValidateSession(p.Token);
        else if (p.ClientId > 0 && db.UserExists(p.ClientId)) userId = p.ClientId;

        if (userId == null)
        {
            Log.Warning("Hello с неизвестным клиентом");
            await Send(ws, new { Type = "Error", Payload = new SimpleStatusPayload { Status = "UnknownClient" } });
            return null;
        }

        clients[userId.Value] = ws;
        string name = db.GetUserName(userId.Value) ?? $"User {userId.Value}";

        Log.Information("Hello: reconnect userId={UserId}", userId.Value);

        await Send(ws, new
        {
            Type = "HelloResponse",
            Payload = new HelloResponsePayload { Status = "Ok", UserId = userId.Value, UserName = name }
        });

        await NotifyOnlineStatus(userId.Value, ws);
        await PushPendingMessages(userId.Value, ws);
        return userId.Value;
    }

    private static async Task PushPendingMessages(int userId, WebSocket ws)
    {
        var pending = db.GetPendingMessages(userId);
        if (pending.Count == 0) return;

        Log.Information("Отправка {Count} недоставленных сообщений userId={UserId}", pending.Count, userId);
        foreach (var hm in pending)
        {
            using (LogContext.PushProperty("MessageId", hm.MessageId))
            {
                try
                {
                    await Send(ws, new
                    {
                        Type = "IncomingMessage",
                        Payload = new IncomingMessagePayload
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
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Не удалось отправить pending messageId={MessageId}", hm.MessageId);
                    return;
                }
            }
        }
    }

    static async Task NotifyOnlineStatus(int userId, WebSocket ws)
    {
        var contactIds = db.GetContactIds(userId);
        foreach (var contactId in contactIds)
        {
            if (clients.ContainsKey(contactId))
                await Send(ws, new { Type = "UserOnline", Payload = new OnlineStatusPayload { UserId = contactId } });
        }
        _ = Task.Run(async () => await BroadcastExcept(userId,
            new { Type = "UserOnline", Payload = new OnlineStatusPayload { UserId = userId } }));
    }

    static async Task ProcessGetContacts(int userId, WebSocket ws)
    {
        var contactIds = db.GetContactIds(userId);
        var contacts = new System.Collections.Generic.List<UserInfo>();
        foreach (var id in contactIds)
        {
            var name = db.GetUserName(id) ?? $"User {id}";
            bool isOnline = clients.ContainsKey(id);
            contacts.Add(new UserInfo { Id = id, Name = name, IsOnline = isOnline });
        }
        Log.Debug("GetContacts userId={UserId} count={Count}", userId, contacts.Count);
        await Send(ws, new { Type = "ContactList", Payload = new ContactListPayload { Contacts = contacts } });
    }

    static async Task ProcessGetPublicKey(BaseMessage msg, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<GetPublicKeyPayload>();
        string? key = p != null ? db.GetPublicKey(p.TargetId) : null;
        await Send(ws, new
        {
            Type = "PublicKeyResponse",
            Payload = new PublicKeyResponsePayload { Status = key != null ? "Ok" : "NotFound", TargetId = p?.TargetId ?? 0, PublicKey = key ?? "" }
        });
    }

    static async Task ProcessGetUserInfo(BaseMessage msg, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<GetUserInfoPayload>();
        string? name = p != null ? db.GetUserName(p.TargetId) : null;
        await Send(ws, new
        {
            Type = "UserInfoResponse",
            Payload = new UserInfoResponsePayload { Status = name != null ? "Ok" : "NotFound", UserId = p?.TargetId ?? 0, UserName = name ?? "" }
        });
    }

    static async Task ProcessSendMessage(BaseMessage msg, int senderId, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<SendMessagePayload>();
        if (p == null) return;

        using (LogContext.PushProperty("CorrelationId", p.MessageId))
        using (LogContext.PushProperty("MessageId", p.MessageId))
        {
            var sw = Stopwatch.StartNew();

            db.SaveMessage(p.MessageId, senderId, p.ReceiverId, p.EncryptedKey,
                p.EncryptedData?.Nonce, p.EncryptedData?.Ciphertext, p.EncryptedData?.Tag,
                p.ContentType, p.FileName, p.FileSize, p.FileTransferId, "Sent", p.EncryptedKeySelf);
            db.AddContact(senderId, p.ReceiverId);

            bool delivered = false;
            if (clients.TryGetValue(p.ReceiverId, out var recvWs) && recvWs.State == WebSocketState.Open)
            {
                await Send(recvWs, new
                {
                    Type = "IncomingMessage",
                    Payload = new IncomingMessagePayload
                    {
                        SenderId = senderId,
                        MessageId = p.MessageId,
                        EncryptedKey = p.EncryptedKey,
                        EncryptedData = p.EncryptedData,
                        ContentType = p.ContentType,
                        FileName = p.FileName,
                        FileSize = p.FileSize,
                        FileTransferId = p.FileTransferId
                    }
                });
                await Send(ws, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "Sent" } });
                delivered = true;
            }
            else
            {
                await Send(ws, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "UserOffline" } });
            }

            sw.Stop();
            var level = sw.ElapsedMilliseconds > 1000 ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Information;
            Log.Write(level, "Message from={Sender} to={Receiver} type={ContentType} size={Size}B delivered={Delivered} in {DurationMs}ms",
                senderId, p.ReceiverId, p.ContentType, p.FileSize, delivered, sw.ElapsedMilliseconds);
        }
    }

    static async Task ProcessAck(BaseMessage msg)
    {
        var p = msg.Payload.Deserialize<AckPayload>();
        if (p != null && p.Status == "Delivered")
        {
            db.UpdateMessageStatus(p.MessageId, "Delivered");
            var senderId = db.GetSenderByMessageId(p.MessageId);
            if (senderId.HasValue && clients.TryGetValue(senderId.Value, out var senderWs))
                await Send(senderWs, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "Delivered" } });
            Log.Debug("Ack Delivered messageId={MessageId}", p.MessageId);
        }
    }

    static async Task ProcessGetHistory(BaseMessage msg, int userId, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<GetHistoryRequestPayload>();
        if (p == null) return;
        var messages = db.GetHistory(userId, p.ContactId);
        Log.Debug("GetHistory userId={UserId} contactId={ContactId} count={Count}", userId, p.ContactId, messages.Count);
        await Send(ws, new { Type = "HistoryResponse", Payload = new HistoryResponsePayload { ContactId = p.ContactId, Messages = messages } });
    }

    static async Task ProcessAddContact(BaseMessage msg, int userId, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<AddContactPayload>();
        if (p == null || !db.UserExists(p.ContactId))
        {
            Log.Warning("AddContact: contactId={ContactId} не найден (userId={UserId})", p?.ContactId, userId);
            await Send(ws, new { Type = "AddContactResponse", Payload = new AddContactResponsePayload { Status = "NotFound", ContactId = p?.ContactId ?? 0 } });
            return;
        }
        db.AddContact(userId, p.ContactId);

        Log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} contactId={ContactId}",
            "AddContact", userId, p.ContactId);

        await Send(ws, new { Type = "AddContactResponse", Payload = new AddContactResponsePayload { Status = "Ok", ContactId = p.ContactId } });
        if (clients.ContainsKey(p.ContactId))
            await Send(ws, new { Type = "UserOnline", Payload = new OnlineStatusPayload { UserId = p.ContactId } });
    }

    static async Task ProcessUpdateProfile(BaseMessage msg, int clientId)
    {
        var p = msg.Payload.Deserialize<UpdateProfilePayload>();
        if (p == null) return;
        db.UpdateUserName(clientId, p.UserName);

        Log.ForContext("Audit", true).Information("AUDIT {Action} userId={UserId} newName={NewName}",
            "ProfileUpdate", clientId, p.UserName);

        var notification = new { Type = "UserProfileUpdated", Payload = new UserProfileUpdatedPayload { UserId = clientId, UserName = p.UserName } };
        foreach (var contactId in db.GetContactIds(clientId))
        {
            if (clients.TryGetValue(contactId, out var contactWs) && contactWs.State == WebSocketState.Open)
            {
                try { await Send(contactWs, notification); } catch { }
            }
        }
    }

    static async Task BroadcastExcept(int? exceptClientId, object message)
    {
        foreach (var kvp in clients)
        {
            if (kvp.Key == exceptClientId) continue;
            try { if (kvp.Value.State == WebSocketState.Open) await Send(kvp.Value, message); }
            catch (Exception ex) { Log.Debug(ex, "BroadcastExcept failed for userId={UserId}", kvp.Key); }
        }
    }

    static async Task Send(WebSocket ws, object obj)
    {
        if (ws.State != WebSocketState.Open) return;
        if (socketLocks.TryGetValue(ws, out var sem))
        {
            await sem.WaitAsync();
            try
            {
                if (ws.State != WebSocketState.Open) return;
                string json = JsonSerializer.Serialize(obj);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally { sem.Release(); }
        }
    }

    internal static string MaskEmail(string email)
    {
        if (string.IsNullOrEmpty(email)) return "";
        int at = email.IndexOf('@');
        if (at <= 0) return "***";
        return email[0] + new string('*', Math.Min(at - 1, 3)) + email.Substring(at);
    }
}