using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
using SecureChat.Common;

namespace SecureChat.Server;

class Program
{
    static ConcurrentDictionary<int, StreamWriter> clients = new();
    static ConcurrentDictionary<string, int> messageSenders = new();
    static Database db = new();

    static async Task Main()
    {
        try
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
        catch (Exception ex)
        {
            Console.WriteLine($"Критическая ошибка сервера: {ex.Message}");
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
            catch (Exception ex)
            {
                Console.WriteLine($"UDP discovery error: {ex.Message}");
            }
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
                if (len <= 0 || len > 100 * 1024 * 1024) break; // увеличен лимит до 100 МБ

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
                    case "SendMessage" when clientId != null:
                        await ProcessSendMessage(msg, clientId.Value, writer); break;
                    case "Ack" when clientId != null:
                        await ProcessAck(msg); break;
                    case "FileChunk" when clientId != null:
                        await ProcessFileChunk(msg, clientId.Value); break;
                    case "GetUsers" when clientId != null:
                        await ProcessGetUsers(writer); break;
                    case "UpdateProfile" when clientId != null:
                        await ProcessUpdateProfile(msg, clientId.Value);
                        break;
                    case "Ping":
                        await Send(writer, new { Type = "Pong" }); break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка обработки клиента {clientId}: {ex.Message}");
        }
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
        if (key == null)
            await Send(writer, new { Type = "PublicKeyResponse", Payload = new PublicKeyResponsePayload { Status = "NotFound", TargetId = p.TargetId } });
        else
            await Send(writer, new { Type = "PublicKeyResponse", Payload = new PublicKeyResponsePayload { Status = "Ok", TargetId = p.TargetId, PublicKey = key } });
    }

    static async Task ProcessSendMessage(BaseMessage msg, int senderId, StreamWriter writer)
    {
        var p = msg.Payload.Deserialize<SendMessagePayload>();
        if (!clients.TryGetValue(p.ReceiverId, out var recvWriter))
        {
            await Send(writer, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "UserOffline" } });
            return;
        }
        messageSenders[p.MessageId] = senderId;
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
        if (p.Status == "Delivered" && messageSenders.TryRemove(p.MessageId, out int senderId))
        {
            if (clients.TryGetValue(senderId, out var sw))
                await Send(sw, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "Delivered" } });
        }
    }

    static async Task ProcessFileChunk(BaseMessage msg, int senderId)
    {
        var p = msg.Payload.Deserialize<FileChunkPayload>();
        if (!clients.TryGetValue(p.ReceiverId, out var sw)) return;
        await Send(sw, new
        {
            Type = "FileChunk",
            Payload = new
            {
                SenderId = senderId,
                FileTransferId = p.FileTransferId,
                ChunkIndex = p.ChunkIndex,
                TotalChunks = p.TotalChunks,
                Offset = p.Offset,
                Length = p.Length,
                Data = p.Data
            }
        });
    }

    static async Task ProcessGetUsers(StreamWriter writer)
    {
        var users = db.GetAllUsers();
        var list = users.Select(u => new UserInfo { Id = u.id, Name = u.name }).ToList();
        await Send(writer, new { Type = "UserList", Payload = new UserListPayload { Users = list } });
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