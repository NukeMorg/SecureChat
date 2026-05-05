using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
using SecureChat.Common;
using Microsoft.Data.Sqlite;

namespace SecureChat.Server;

class Program
{
    static ConcurrentDictionary<int, StreamWriter> clients = new();
    static Database db = new();

    

    static async Task Main()
    {
        db.Initialize();
        var listener = new TcpListener(IPAddress.Any, 9000);
        listener.Start();
        Console.WriteLine("Сервер запущен на порту 9000");
        Console.WriteLine("UDP discovery запущен на порту 9001");

        _ = Task.Run(StartUdpDiscovery);

        while (true)
        {
            var tcp = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleClient(tcp));
        }
    }

    static async Task StartUdpDiscovery()
    {
        using var udp = new UdpClient(9001);
        while (true)
        {
            try
            {
                var result = await udp.ReceiveAsync();
                string msg = Encoding.UTF8.GetString(result.Buffer);
                if (msg == "DISCOVER_SECURECHAT_SERVER")
                {
                    byte[] response = Encoding.UTF8.GetBytes("SECURECHAT_SERVER_RESPONSE");
                    await udp.SendAsync(response, response.Length, result.RemoteEndPoint);
                    Console.WriteLine($"Обнаружен запрос от {result.RemoteEndPoint}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"UDP discovery error: {ex.Message}"); }
        }
    }

    static async Task HandleClient(TcpClient tcp)
    {
        var stream = tcp.GetStream();
        var reader = new StreamReader(stream);
        var writer = new StreamWriter(stream) { AutoFlush = true };
        int? clientId = null;

        try
        {
            while (tcp.Connected)
            {
                byte[] lenBuf = new byte[4];
                int read = await stream.ReadAsync(lenBuf, 0, 4);
                if (read < 4) break;
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len <= 0 || len > 100 * 1024 * 1024) break;

                char[] jsonBuf = new char[len];
                int total = 0;
                while (total < len)
                {
                    int r = await reader.ReadAsync(jsonBuf, total, len - total);
                    if (r == 0) break;
                    total += r;
                }
                string json = new(jsonBuf, 0, total);
                var msg = JsonSerializer.Deserialize<BaseMessage>(json);
                if (msg == null) continue;

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
                    case "Ping":
                        await Send(writer, new { Type = "Pong" }); break;
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"Ошибка обработки клиента {clientId}: {ex.Message}"); }
        finally
        {
            if (clientId.HasValue) clients.TryRemove(clientId.Value, out _);
            try { tcp.Close(); } catch { }
        }
    }

    static async Task ProcessRegister(BaseMessage msg, StreamWriter writer)
    {
        var p = msg.Payload.Deserialize<RegisterPayload>();
        int id = db.AddUser(p.PublicKey);
        await Send(writer, new { Type = "RegisterResponse", Payload = new RegisterResponsePayload { Status = "Ok", AssignedId = id } });
    }

    static async Task<int?> ProcessHello(BaseMessage msg, StreamWriter writer, int? current)
    {
        var p = msg.Payload.Deserialize<HelloPayload>();
        if (!db.UserExists(p.ClientId))
        {
            await Send(writer, new { Type = "Error", Payload = new SimpleStatusPayload { Status = "UnknownClient" } });
            return null;
        }
        if (clients.TryGetValue(p.ClientId, out var old))
        {
            try { old.Dispose(); } catch { }
            clients.TryRemove(p.ClientId, out _);
        }
        clients[p.ClientId] = writer;
        await Send(writer, new { Type = "HelloResponse", Payload = new { Status = "Ok" } });
        return p.ClientId;
    }

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

    static async Task ProcessSendMessage(BaseMessage msg, int senderId, StreamWriter writer)
    {
        var p = msg.Payload.Deserialize<SendMessagePayload>();
        // сохраняем в БД
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
            "Sent"
        );
        // добавляем контакты
        db.AddContact(senderId, p.ReceiverId);

        if (!clients.TryGetValue(p.ReceiverId, out var recvWriter))
        {
            await Send(writer, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "UserOffline" } });
            return;
        }
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

    static async Task ProcessAck(BaseMessage msg)
    {
        var p = msg.Payload.Deserialize<AckPayload>();
        if (p.Status == "Delivered")
        {
            db.UpdateMessageStatus(p.MessageId, "Delivered");
            // оповещаем отправителя, если он онлайн
            var senderId = GetSenderByMessageId(p.MessageId);
            if (senderId.HasValue && clients.TryGetValue(senderId.Value, out var sw))
            {
                await Send(sw, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "Delivered" } });
            }
        }
    }

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

    static async Task ProcessGetContacts(int userId, StreamWriter writer)
    {
        var contactIds = db.GetContactIds(userId);
        var contacts = new List<UserInfo>();
        foreach (var id in contactIds)
        {
            var name = db.GetUserName(id) ?? $"User {id}";
            contacts.Add(new UserInfo { Id = id, Name = name });
        }
        await Send(writer, new
        {
            Type = "ContactList",
            Payload = new ContactListPayload { Contacts = contacts }
        });
    }

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

    static async Task Send(StreamWriter writer, object obj)
    {
        string json = JsonSerializer.Serialize(obj);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        byte[] len = BitConverter.GetBytes(bytes.Length);
        await writer.BaseStream.WriteAsync(len, 0, 4);
        await writer.BaseStream.WriteAsync(bytes, 0, bytes.Length);
    }
}