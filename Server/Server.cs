using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
using SecureChat.Common;
using Microsoft.Data.Sqlite;

namespace SecureChat.Server;

// Главный класс сервера: прослушивает TCP-порт 9000,
// обрабатывает клиентов, маршрутизирует сообщения.
class Server
{
    // Подключённые клиенты: ID -> StreamWriter для отправки
    static ConcurrentDictionary<int, StreamWriter> clients = new();
    static Database db = new();

    static async Task Main()
    {
        db.Initialize();
        var listener = new TcpListener(IPAddress.Any, 9000);
        listener.Start();
        Console.WriteLine("Сервер запущен на порту 9000");


        while (true)
        {
            var tcp = await listener.AcceptTcpClientAsync();
            // Каждого клиента обрабатываем в отдельной задаче
            _ = Task.Run(() => HandleClient(tcp));
        }
    }

    

    // Обрабатывает одно TCP-соединение: читает сообщения в цикле,
    // вызывает соответствующие обработчики по типу сообщения.
    static async Task HandleClient(TcpClient tcp)
    {
        var stream = tcp.GetStream();
        var writer = new StreamWriter(stream) { AutoFlush = true };
        int? clientId = null;

        try
        {
            while (tcp.Connected)
            {
                // Читаем длину сообщения (4 байта)
                byte[] lenBuf = new byte[4];
                int read = await stream.ReadAsync(lenBuf, 0, 4);
                if (read < 4) break;
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len <= 0 || len > 100 * 1024 * 1024) break; // защита от невалидных данных

                // Читаем JSON сообщения
                byte[] jsonBytes = new byte[len];
                int total = 0;
                while (total < len)
                {
                    int r = await stream.ReadAsync(jsonBytes, total, len - total);
                    if (r == 0) break;
                    total += r;
                }
                string json = Encoding.UTF8.GetString(jsonBytes, 0, total);
                var msg = JsonSerializer.Deserialize<BaseMessage>(json);
                if (msg == null) continue;

                // Маршрутизация по типу сообщения
                switch (msg.Type)
                {
                    case "Register": await ProcessRegister(msg, writer); break;
                    case "Hello":
                        clientId = await ProcessHello(msg, writer, clientId);
                        if (clientId != null) clients[clientId.Value] = writer;
                        break;
                    case "GetPublicKey" when clientId != null:
                        await ProcessGetPublicKey(msg, writer); break;
                    case "GetUserInfo" when clientId != null:
                        await ProcessGetUserInfo(msg, writer); break;
                    case "SendMessage" when clientId != null:
                        await ProcessSendMessage(msg, clientId.Value, writer); break;
                    case "Ack" when clientId != null:
                        await ProcessAck(msg); break;
                    case "GetHistory" when clientId != null:
                        await ProcessGetHistory(msg, clientId.Value, writer); break;
                    case "GetContacts" when clientId != null:
                        await ProcessGetContacts(clientId.Value, writer); break;
                    case "UpdateProfile" when clientId != null:
                        await ProcessUpdateProfile(msg, clientId.Value); break;
                    case "AddContact" when clientId != null:
                        await ProcessAddContact(msg, clientId.Value); break;
                    case "Ping":
                        await Send(writer, new { Type = "Pong" }); break;
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"Ошибка обработки клиента {clientId}: {ex.Message}"); }
        finally
        {
            if (clientId.HasValue) clients.TryRemove(clientId.Value, out _);
            if (clientId.HasValue)
            {
                // Уведомляем остальных, что пользователь отключился
                _ = Task.Run(async () =>
                    await BroadcastExcept(clientId.Value, new { Type = "UserOffline", Payload = new OnlineStatusPayload { UserId = clientId.Value } }));
            }
            try { tcp.Close(); } catch { }
        }
    }

    // Рассылает сообщение всем клиентам, кроме указанного (exceptClientId).
    static async Task BroadcastExcept(int? exceptClientId, object message)
    {
        foreach (var kvp in clients)
        {
            if (kvp.Key == exceptClientId) continue;
            try
            {
                await Send(kvp.Value, message);
            }
            catch { /* клиент мог уже отключиться */ }
        }
    }

    // Отправляет клиенту список его контактов с серверными данными (имя, онлайн).
    static async Task ProcessGetContacts(int userId, StreamWriter writer)
    {
        var contactIds = db.GetContactIds(userId);
        var contacts = new List<UserInfo>();
        foreach (var id in contactIds)
        {
            var name = db.GetUserName(id) ?? $"User {id}";
            bool isOnline = clients.ContainsKey(id);               // <-- проверка
            contacts.Add(new UserInfo { Id = id, Name = name, IsOnline = isOnline });
        }
        await Send(writer, new
        {
            Type = "ContactList",
            Payload = new ContactListPayload { Contacts = contacts }
        });
    }

    // Регистрация нового пользователя: сохраняет публичный ключ, возвращает ID.
    static async Task ProcessRegister(BaseMessage msg, StreamWriter writer)
    {
        var p = msg.Payload.Deserialize<RegisterPayload>();
        int id = db.AddUser(p.PublicKey);
        await Send(writer, new { Type = "RegisterResponse", Payload = new RegisterResponsePayload { Status = "Ok", AssignedId = id } });
    }

    // Обработка Hello (вход в сеть):
    // проверяет существование пользователя, регистрирует текущее соединение,
    // отправляет статусы онлайна контактов и оповещает остальных.
    static async Task<int?> ProcessHello(BaseMessage msg, StreamWriter writer, int? current)
    {
        var p = msg.Payload.Deserialize<HelloPayload>();
        if (!db.UserExists(p.ClientId))
        {
            await Send(writer, new { Type = "Error", Payload = new SimpleStatusPayload { Status = "UnknownClient" } });
            return null;
        }
        // Если уже было соединение с этим ID, закрываем старое
        if (clients.TryGetValue(p.ClientId, out var old))
        {
            try { old.Dispose(); } catch { }
            clients.TryRemove(p.ClientId, out _);
        }
        clients[p.ClientId] = writer;
        await Send(writer, new { Type = "HelloResponse", Payload = new { Status = "Ok" } });

        // === ИЗМЕНЕНИЕ: отправляем подключившемуся клиенту статусы "онлайн" для всех контактов, которые в сети сейчас ===
        var contactIds = db.GetContactIds(p.ClientId);
        foreach (var contactId in contactIds)
        {
            if (clients.ContainsKey(contactId))  // контакт онлайн
            {
                await Send(writer, new
                {
                    Type = "UserOnline",
                    Payload = new OnlineStatusPayload { UserId = contactId }
                });
            }
        }

        // Уведомляем остальных, что пользователь в сети
        _ = Task.Run(async () =>
            await BroadcastExcept(p.ClientId, new { Type = "UserOnline", Payload = new OnlineStatusPayload { UserId = p.ClientId } }));

        return p.ClientId;
    }

    // Возвращает публичный ключ запрошенного пользователя.
    static async Task ProcessGetPublicKey(BaseMessage msg, StreamWriter writer)
    {
        var p = msg.Payload.Deserialize<GetPublicKeyPayload>();
        string? key = db.GetPublicKey(p.TargetId);
        await Send(writer, new
        {
            Type = "PublicKeyResponse",
            Payload = new PublicKeyResponsePayload
            {
                Status = key != null ? "Ok" : "NotFound",
                TargetId = p.TargetId,
                PublicKey = key ?? ""
            }
        });
    }

    // Возвращает имя запрошенного пользователя.
    static async Task ProcessGetUserInfo(BaseMessage msg, StreamWriter writer)
    {
        var p = msg.Payload.Deserialize<GetUserInfoPayload>();
        string? name = db.GetUserName(p.TargetId);
        await Send(writer, new
        {
            Type = "UserInfoResponse",
            Payload = new UserInfoResponsePayload
            {
                Status = name != null ? "Ok" : "NotFound",
                UserId = p.TargetId,
                UserName = name ?? ""
            }
        });
    }

    // Обрабатывает отправку сообщения:
    // сохраняет в БД, при необходимости добавляет контакт,
    // доставляет получателю (если онлайн) или возвращает статус "UserOffline".
    static async Task ProcessSendMessage(BaseMessage msg, int senderId, StreamWriter writer)
    {
        var p = msg.Payload.Deserialize<SendMessagePayload>();
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
            p.EncryptedKeySelf    // === ИЗМЕНЕНИЕ: передаём новый параметр ===
        );
        // Автоматически добавляем контакт, если его ещё нет
        db.AddContact(senderId, p.ReceiverId);

        if (!clients.TryGetValue(p.ReceiverId, out var recvWriter))
        {
            await Send(writer, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "UserOffline" } });
            return;
        }
        // Доставляем сообщение получателю
        await Send(recvWriter, new
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
        await Send(writer, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "Sent" } });
    }

    // Обрабатывает подтверждение о доставке (Ack) от получателя.
    // Обновляет статус в БД и уведомляет отправителя.
    static async Task ProcessAck(BaseMessage msg)
    {
        var p = msg.Payload.Deserialize<AckPayload>();
        if (p.Status == "Delivered")
        {
            db.UpdateMessageStatus(p.MessageId, "Delivered");
            var senderId = GetSenderByMessageId(p.MessageId);
            if (senderId.HasValue && clients.TryGetValue(senderId.Value, out var sw))
            {
                await Send(sw, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "Delivered" } });
            }
        }
    }

    // Получает ID отправителя по MessageId.
    static int? GetSenderByMessageId(string messageId)
    {
        using var conn = new SqliteConnection(db.ConnectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SenderId FROM Messages WHERE MessageId = @id";
        cmd.Parameters.AddWithValue("@id", messageId);
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToInt32(result) : (int?)null;
    }

    // Возвращает историю сообщений с конкретным контактом.
    static async Task ProcessGetHistory(BaseMessage msg, int userId, StreamWriter writer)
    {
        var p = msg.Payload.Deserialize<GetHistoryRequestPayload>();
        var messages = db.GetHistory(userId, p.ContactId);
        await Send(writer, new
        {
            Type = "HistoryResponse",
            Payload = new HistoryResponsePayload { ContactId = p.ContactId, Messages = messages }
        });
    }

    // Добавляет контакт (если целевой пользователь существует).
    static async Task ProcessAddContact(BaseMessage msg, int userId)
    {
        var p = msg.Payload.Deserialize<AddContactPayload>();
        if (!db.UserExists(p.ContactId))
        {
            await Send(clients[userId], new
            {
                Type = "AddContactResponse",
                Payload = new AddContactResponsePayload { Status = "NotFound", ContactId = p.ContactId }
            });
            return;
        }
        db.AddContact(userId, p.ContactId);
        await Send(clients[userId], new
        {
            Type = "AddContactResponse",
            Payload = new AddContactResponsePayload { Status = "Ok", ContactId = p.ContactId }
        });
    }

    // Обновляет имя пользователя и рассылает всем уведомление.
    static async Task ProcessUpdateProfile(BaseMessage msg, int clientId)
    {
        var p = msg.Payload.Deserialize<UpdateProfilePayload>();
        db.UpdateUserName(clientId, p.UserName);
        var notification = new
        {
            Type = "UserProfileUpdated",
            Payload = new UserProfileUpdatedPayload { UserId = clientId, UserName = p.UserName }
        };
        foreach (var sw in clients.Values)
            await Send(sw, notification);
    }

    // Отправляет JSON-сообщение через StreamWriter с префиксом длины (4 байта).
    static async Task Send(StreamWriter writer, object obj)
    {
        string json = JsonSerializer.Serialize(obj);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        byte[] len = BitConverter.GetBytes(bytes.Length);
        await writer.BaseStream.WriteAsync(len, 0, 4);
        await writer.BaseStream.WriteAsync(bytes, 0, bytes.Length);
    }
}