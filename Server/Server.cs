using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SecureChat.Common;

namespace SecureChat.Server;

// Сервер SecureChat на WebSocket (замена TcpListener) с корпоративной авторизацией
class Server
{
    static ConcurrentDictionary<int, WebSocket> clients = new();  // Активные подключения: userId -> ws
    static Database db = new();                                   // Слой хранения данных

    static async Task Main()
    {
        db.Initialize();

        // Привязка к порту 9000 (все интерфейсы + localhost)
        var listener = new HttpListener();
        listener.Prefixes.Add("http://*:9000/");
        listener.Prefixes.Add("http://localhost:9000/");
        try
        {
            listener.Start();
            Console.WriteLine("WebSocket сервер SecureChat запущен на ws://*:9000/");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка запуска HttpListener: {ex.Message}");
            // Fallback: только localhost
            listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:9000/");
            listener.Start();
            Console.WriteLine("WebSocket сервер SecureChat запущен на ws://localhost:9000/");
        }

        // Основной цикл приёма HTTP-запросов
        while (true)
        {
            try
            {
                var context = await listener.GetContextAsync();
                if (context.Request.IsWebSocketRequest)
                {
                    // Апгрейд до WebSocket в отдельной задаче
                    _ = Task.Run(async () =>
                    {
                        HttpListenerWebSocketContext wsContext;
                        try
                        {
                            wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
                        }
                        catch
                        {
                            context.Response.StatusCode = 500;
                            context.Response.Close();
                            return;
                        }
                        await HandleClient(wsContext.WebSocket);
                    });
                }
                else
                {
                    // Простой healthcheck для HTTP
                    byte[] response = Encoding.UTF8.GetBytes("SecureChat WebSocket Server is Running");
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    context.Response.ContentLength64 = response.Length;
                    await context.Response.OutputStream.WriteAsync(response, 0, response.Length);
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка прослушивания соединений: {ex.Message}");
            }
        }
    }

    // Цикл обработки одного клиентского WS-соединения
    static async Task HandleClient(WebSocket ws)
    {
        int? clientId = null;
        byte[] buffer = new byte[64 * 1024];

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                // Накопление фрагментированного сообщения
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

                // Разбор конверта и маршрутизация по типу
                string json = Encoding.UTF8.GetString(ms.ToArray());
                var msg = JsonSerializer.Deserialize<BaseMessage>(json);
                if (msg == null) continue;

                switch (msg.Type)
                {
                    case "Login":
                        clientId = await ProcessLogin(msg, ws);
                        break;
                    case "RegisterAccount":
                        clientId = await ProcessRegisterAccount(msg, ws);
                        break;
                    case "Hello":
                        clientId = await ProcessHello(msg, ws, clientId);
                        break;
                    case "GetPublicKey" when clientId != null:
                        await ProcessGetPublicKey(msg, ws);
                        break;
                    case "GetUserInfo" when clientId != null:
                        await ProcessGetUserInfo(msg, ws);
                        break;
                    case "SendMessage" when clientId != null:
                        await ProcessSendMessage(msg, clientId.Value, ws);
                        break;
                    case "Ack" when clientId != null:
                        await ProcessAck(msg);
                        break;
                    case "GetHistory" when clientId != null:
                        await ProcessGetHistory(msg, clientId.Value, ws);
                        break;
                    case "GetContacts" when clientId != null:
                        await ProcessGetContacts(clientId.Value, ws);
                        break;
                    case "UpdateProfile" when clientId != null:
                        await ProcessUpdateProfile(msg, clientId.Value);
                        break;
                    case "AddContact" when clientId != null:
                        await ProcessAddContact(msg, clientId.Value, ws);
                        break;
                    case "Ping":
                        await Send(ws, new { Type = "Pong" });
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка в соединении клиента {clientId}: {ex.Message}");
        }
        finally
        {
            // Очистка при отключении: снять клиента и оповестить остальных
            if (clientId.HasValue)
            {
                clients.TryRemove(clientId.Value, out _);
                _ = Task.Run(async () =>
                    await BroadcastExcept(clientId.Value, new { Type = "UserOffline", Payload = new OnlineStatusPayload { UserId = clientId.Value } }));
            }
            try { ws.Dispose(); } catch { }
        }
    }

    // Авторизация по логину/паролю
    static async Task<int?> ProcessLogin(BaseMessage msg, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<LoginPayload>();
        if (p == null) return null;

        var authResult = db.Authenticate(p.Login, p.Password);
        if (authResult == null)
        {
            await Send(ws, new { Type = "LoginResponse", Payload = new LoginResponsePayload { Status = "InvalidCredentials" } });
            return null;
        }

        int userId = authResult.Value.id;
        string userName = authResult.Value.name;
        string token = authResult.Value.token;

        clients[userId] = ws;

        await Send(ws, new
        {
            Type = "LoginResponse",
            Payload = new LoginResponsePayload
            {
                Status = "Ok",
                UserId = userId,
                UserName = userName,
                Token = token
            }
        });

        await NotifyOnlineStatus(userId, ws);
        return userId;
    }

    // Регистрация нового пользователя с публичным ключом
    static async Task<int?> ProcessRegisterAccount(BaseMessage msg, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<RegisterAccountPayload>();
        if (p == null) return null;

        var regResult = db.RegisterUser(p.Login, p.Password, p.UserName, p.PublicKey);
        if (regResult == null)
        {
            await Send(ws, new { Type = "RegisterAccountResponse", Payload = new RegisterAccountResponsePayload { Status = "UserExists" } });
            return null;
        }

        int userId = regResult.Value.id;
        string token = regResult.Value.token;

        clients[userId] = ws;

        await Send(ws, new
        {
            Type = "RegisterAccountResponse",
            Payload = new RegisterAccountResponsePayload
            {
                Status = "Ok",
                AssignedId = userId,
                Token = token
            }
        });

        await NotifyOnlineStatus(userId, ws);
        return userId;
    }

    // Переавторизация по токену (или ClientId) без пароля
    static async Task<int?> ProcessHello(BaseMessage msg, WebSocket ws, int? current)
    {
        var p = msg.Payload.Deserialize<HelloPayload>();
        if (p == null) return null;

        int? userId = null;
        if (!string.IsNullOrEmpty(p.Token))
        {
            userId = db.ValidateSession(p.Token);
        }
        else if (p.ClientId > 0 && db.UserExists(p.ClientId))
        {
            userId = p.ClientId;
        }

        if (userId == null)
        {
            await Send(ws, new { Type = "Error", Payload = new SimpleStatusPayload { Status = "UnknownClient" } });
            return null;
        }

        clients[userId.Value] = ws;
        string name = db.GetUserName(userId.Value) ?? $"User {userId.Value}";

        await Send(ws, new
        {
            Type = "HelloResponse",
            Payload = new HelloResponsePayload
            {
                Status = "Ok",
                UserId = userId.Value,
                UserName = name
            }
        });

        await NotifyOnlineStatus(userId.Value, ws);
        return userId.Value;
    }

    // Информирование о смене онлайн-статуса (себе — о контактах, остальным — о себе)
    static async Task NotifyOnlineStatus(int userId, WebSocket ws)
    {
        var contactIds = db.GetContactIds(userId);
        foreach (var contactId in contactIds)
        {
            if (clients.ContainsKey(contactId))
            {
                await Send(ws, new { Type = "UserOnline", Payload = new OnlineStatusPayload { UserId = contactId } });
            }
        }

        _ = Task.Run(async () =>
            await BroadcastExcept(userId, new { Type = "UserOnline", Payload = new OnlineStatusPayload { UserId = userId } }));
    }

    // Список контактов с онлайн-статусом
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
        await Send(ws, new { Type = "ContactList", Payload = new ContactListPayload { Contacts = contacts } });
    }

    // Выдача публичного RSA-ключа получателя
    static async Task ProcessGetPublicKey(BaseMessage msg, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<GetPublicKeyPayload>();
        string? key = p != null ? db.GetPublicKey(p.TargetId) : null;
        await Send(ws, new
        {
            Type = "PublicKeyResponse",
            Payload = new PublicKeyResponsePayload
            {
                Status = key != null ? "Ok" : "NotFound",
                TargetId = p?.TargetId ?? 0,
                PublicKey = key ?? ""
            }
        });
    }

    // Информация о пользователе (имя)
    static async Task ProcessGetUserInfo(BaseMessage msg, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<GetUserInfoPayload>();
        string? name = p != null ? db.GetUserName(p.TargetId) : null;
        await Send(ws, new
        {
            Type = "UserInfoResponse",
            Payload = new UserInfoResponsePayload
            {
                Status = name != null ? "Ok" : "NotFound",
                UserId = p?.TargetId ?? 0,
                UserName = name ?? ""
            }
        });
    }

    // Сохранение сообщения и доставка получателю, если он онлайн
    static async Task ProcessSendMessage(BaseMessage msg, int senderId, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<SendMessagePayload>();
        if (p == null) return;

        // Сохраняем в БД (шифртекст, ключи, метаданные)
        db.SaveMessage(
            p.MessageId,
            senderId,
            p.ReceiverId,
            p.EncryptedKey,
            p.EncryptedData?.Nonce,
            p.EncryptedData?.Ciphertext,
            p.EncryptedData?.Tag,
            p.ContentType,
            p.FileName,
            p.FileSize,
            p.FileTransferId,
            "Sent",
            p.EncryptedKeySelf
        );
        db.AddContact(senderId, p.ReceiverId);

        // Получатель офлайн — сообщаем отправителю
        if (!clients.TryGetValue(p.ReceiverId, out var recvWs) || recvWs.State != WebSocketState.Open)
        {
            await Send(ws, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "UserOffline" } });
            return;
        }

        // Доставка получателю
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
    }

    // Обработка подтверждения: отметка "Delivered" и уведомление отправителя
    static async Task ProcessAck(BaseMessage msg)
    {
        var p = msg.Payload.Deserialize<AckPayload>();
        if (p != null && p.Status == "Delivered")
        {
            db.UpdateMessageStatus(p.MessageId, "Delivered");
            var senderId = db.GetSenderByMessageId(p.MessageId);
            if (senderId.HasValue && clients.TryGetValue(senderId.Value, out var senderWs))
            {
                await Send(senderWs, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "Delivered" } });
            }
        }
    }

    // История переписки между двумя пользователями
    static async Task ProcessGetHistory(BaseMessage msg, int userId, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<GetHistoryRequestPayload>();
        if (p == null) return;
        var messages = db.GetHistory(userId, p.ContactId);
        await Send(ws, new { Type = "HistoryResponse", Payload = new HistoryResponsePayload { ContactId = p.ContactId, Messages = messages } });
    }

    // Добавление контакта (с уведомлением об онлайн-статусе)
    static async Task ProcessAddContact(BaseMessage msg, int userId, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<AddContactPayload>();
        if (p == null || !db.UserExists(p.ContactId))
        {
            await Send(ws, new { Type = "AddContactResponse", Payload = new AddContactResponsePayload { Status = "NotFound", ContactId = p?.ContactId ?? 0 } });
            return;
        }
        db.AddContact(userId, p.ContactId);
        await Send(ws, new { Type = "AddContactResponse", Payload = new AddContactResponsePayload { Status = "Ok", ContactId = p.ContactId } });
        if (clients.ContainsKey(p.ContactId))
        {
            await Send(ws, new { Type = "UserOnline", Payload = new OnlineStatusPayload { UserId = p.ContactId } });
        }
    }

    // Смена имени пользователя + рассылка всем клиентам
    static async Task ProcessUpdateProfile(BaseMessage msg, int clientId)
    {
        var p = msg.Payload.Deserialize<UpdateProfilePayload>();
        if (p == null) return;
        db.UpdateUserName(clientId, p.UserName);
        var notification = new { Type = "UserProfileUpdated", Payload = new UserProfileUpdatedPayload { UserId = clientId, UserName = p.UserName } };
        foreach (var clientWs in clients.Values)
        {
            await Send(clientWs, notification);
        }
    }

    // Широковещательная рассылка всем, кроме одного пользователя
    static async Task BroadcastExcept(int? exceptClientId, object message)
    {
        foreach (var kvp in clients)
        {
            if (kvp.Key == exceptClientId) continue;
            try
            {
                if (kvp.Value.State == WebSocketState.Open)
                {
                    await Send(kvp.Value, message);
                }
            }
            catch { }
        }
    }

    // Утилита: сериализация и отправка в WS
    static async Task Send(WebSocket ws, object obj)
    {
        if (ws.State != WebSocketState.Open) return;
        string json = JsonSerializer.Serialize(obj);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}