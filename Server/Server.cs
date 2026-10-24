using Microsoft.Extensions.Configuration;
using SecureChat.Common;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SecureChat.Server;

/// <summary>
/// WebSocket-сервер SecureChat: принимает подключения и маршрутизирует сообщения по Type.
/// </summary>
class Server
{
    // Подключённые клиенты: UserId -> WebSocket
    static ConcurrentDictionary<int, WebSocket> clients = new();
    // Персональные семафоры на сокет — защищают SendAsync от параллельных вызовов
    static ConcurrentDictionary<WebSocket, SemaphoreSlim> socketLocks = new();
    static Database db = new();
    // Без инициализации — заполним через Initialize
    static EmailService emailService = null!;

    // Вызывается один раз при старте приложения
    public static void Initialize(IConfiguration configuration)
    {
        emailService = new EmailService(configuration);
    }

    static async Task Main()
    {
        // 1. Собираем конфигурацию: user secrets + переменные окружения
        var configuration = new ConfigurationBuilder()
                    .AddUserSecrets<Server>(optional: true)
                    .AddEnvironmentVariables()
                    .Build();

        // 2. Передаём её в EmailService
        Initialize(configuration);

        // 3. Диагностика: покажет, подхватились ли креды (без вывода самого пароля)
        var email = configuration["Smtp:SenderEmail"];
        var password = configuration["Smtp:SenderPassword"];
        Console.WriteLine($"[EMAIL INIT] sender={(string.IsNullOrWhiteSpace(email) ? "<NOT SET>" : email)}, password={(string.IsNullOrWhiteSpace(password) ? "<NOT SET>" : "<set>")}");

        db.Initialize();

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
            // Фолбэк на localhost, если нет прав на привязку к *
            Console.WriteLine($"Предупреждение: {ex.Message}. Запуск на ws://localhost:9000/");
            listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:9000/");
            listener.Start();
            Console.WriteLine("WebSocket сервер SecureChat запущен на ws://localhost:9000/");
        }

        // Основной цикл приёма подключений
        while (true)
        {
            try
            {
                var context = await listener.GetContextAsync();
                if (context.Request.IsWebSocketRequest)
                {
                    // Каждый клиент — в отдельной задаче
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
                    // Обычный HTTP-запрос — health-check строка
                    byte[] response = Encoding.UTF8.GetBytes("SecureChat WebSocket Server is Running");
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    context.Response.ContentLength64 = response.Length;
                    await context.Response.OutputStream.WriteAsync(response, 0, response.Length);
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка HttpListener: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Читает сообщения одного клиента, диспетчеризует по Type.
    /// </summary>
    static async Task HandleClient(WebSocket ws)
    {
        int? clientId = null;
        byte[] buffer = new byte[64 * 1024];
        // Создаём семафор для этого сокета (для безопасной параллельной отправки)
        socketLocks[ws] = new SemaphoreSlim(1, 1);

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                // Склеиваем фрагменты одного сообщения
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

                // Маршрутизация запросов (обработчики с clientId требуют авторизации)
                switch (msg.Type)
                {
                    case "SendEmailCode":
                        await ProcessSendEmailCode(msg, ws);
                        break;
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
            Console.WriteLine($"Разрыв соединения клиента {clientId}: {ex.Message}");
        }
        finally
        {
            // Убираем клиента из списка и оповещаем остальных об оффлайне
            if (clientId.HasValue)
            {
                clients.TryRemove(clientId.Value, out _);
                _ = Task.Run(async () =>
                    await BroadcastExcept(clientId.Value, new { Type = "UserOffline", Payload = new OnlineStatusPayload { UserId = clientId.Value } }));
            }

            // Освобождаем семафор сокета
            if (socketLocks.TryRemove(ws, out var sem))
            {
                sem.Dispose();
            }

            try { ws.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Генерация и отправка кода подтверждения на email.
    /// </summary>
    static async Task ProcessSendEmailCode(BaseMessage msg, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<SendEmailCodePayload>();
        if (p == null || string.IsNullOrWhiteSpace(p.Email) || !p.Email.Contains('@'))
        {
            await Send(ws, new { Type = "SendEmailCodeResponse", Payload = new SendEmailCodeResponsePayload { Status = "InvalidEmail", Message = "Некорректный адрес почты." } });
            return;
        }

        string code = emailService.GenerateVerificationCode();
        // Код живёт 10 минут
        db.SaveVerificationCode(p.Email, code, TimeSpan.FromMinutes(10));

        bool sent = await emailService.SendVerificationCodeAsync(p.Email, code);
        if (sent)
        {
            await Send(ws, new { Type = "SendEmailCodeResponse", Payload = new SendEmailCodeResponsePayload { Status = "Ok", Message = "Код подтверждения отправлен на почту." } });
        }
        else
        {
            await Send(ws, new { Type = "SendEmailCodeResponse", Payload = new SendEmailCodeResponsePayload { Status = "SendFailed", Message = "Не удалось отправить письмо. Проверьте адрес или консоль сервера." } });
        }
    }

    /// <summary>
    /// Обрабатывает вход по email/паролю, создаёт сессию.
    /// </summary>
    static async Task<int?> ProcessLogin(BaseMessage msg, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<LoginPayload>();
        if (p == null || string.IsNullOrWhiteSpace(p.Email))
        {
            await Send(ws, new { Type = "LoginResponse", Payload = new LoginResponsePayload { Status = "InvalidCredentials" } });
            return null;
        }

        Console.WriteLine($"Попытка входа: {p.Email}");
        var authResult = db.Authenticate(p.Email, p.Password);
        if (authResult == null)
        {
            Console.WriteLine($"Неверные учетные данные для: {p.Email}");
            await Send(ws, new { Type = "LoginResponse", Payload = new LoginResponsePayload { Status = "InvalidCredentials" } });
            return null;
        }

        int userId = authResult.Value.id;
        string userName = authResult.Value.name;
        string token = authResult.Value.token;

        clients[userId] = ws;
        Console.WriteLine($"Успешный вход: {p.Email} (ID: {userId})");

        // Сначала ответ клиенту
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

        // Затем рассылаем онлайн-статусы
        await NotifyOnlineStatus(userId, ws);
        return userId;
    }

    /// <summary>
    /// Регистрация: сначала проверяем код из письма, затем создаём аккаунт.
    /// </summary>
    static async Task<int?> ProcessRegisterAccount(BaseMessage msg, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<RegisterAccountPayload>();
        if (p == null || string.IsNullOrWhiteSpace(p.Email))
        {
            await Send(ws, new { Type = "RegisterAccountResponse", Payload = new RegisterAccountResponsePayload { Status = "Error" } });
            return null;
        }

        Console.WriteLine($"Запрос на регистрацию: {p.Email}");

        // 1. Проверяем код подтверждения почты (одноразовый)
        var verifyResult = db.VerifyAndConsumeCode(p.Email, p.Code);
        if (verifyResult == Database.CodeVerifyResult.Invalid)
        {
            Console.WriteLine($"Неверный код для: {p.Email}");
            await Send(ws, new { Type = "RegisterAccountResponse", Payload = new RegisterAccountResponsePayload { Status = "InvalidCode" } });
            return null;
        }
        if (verifyResult == Database.CodeVerifyResult.Expired)
        {
            Console.WriteLine($"Срок действия кода истек для: {p.Email}");
            await Send(ws, new { Type = "RegisterAccountResponse", Payload = new RegisterAccountResponsePayload { Status = "CodeExpired" } });
            return null;
        }

        // 2. Создаём учётную запись
        var regResult = db.RegisterUser(p.Email, p.Password, p.UserName, p.PublicKey);
        if (regResult == null)
        {
            Console.WriteLine($"Пользователь уже существует: {p.Email}");
            await Send(ws, new { Type = "RegisterAccountResponse", Payload = new RegisterAccountResponsePayload { Status = "UserExists" } });
            return null;
        }

        int userId = regResult.Value.id;
        string token = regResult.Value.token;

        clients[userId] = ws;
        Console.WriteLine($"Зарегистрирован и подтвержден новый пользователь ID: {userId}, Email: {p.Email}");

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

    /// <summary>
    /// Повторное подключение уже известного клиента (по токену или ClientId).
    /// </summary>
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

    /// <summary>
    /// Рассылает уведомление о том, что пользователь онлайн (себе — контакты, остальным — себя).
    /// </summary>
    static async Task NotifyOnlineStatus(int userId, WebSocket ws)
    {
        var contactIds = db.GetContactIds(userId);
        foreach (var contactId in contactIds)
        {
            if (clients.ContainsKey(contactId))
            {
                // Отправляем новому клиенту статус его онлайн-контактов
                await Send(ws, new { Type = "UserOnline", Payload = new OnlineStatusPayload { UserId = contactId } });
            }
        }

        // Остальным — что этот пользователь онлайн
        _ = Task.Run(async () =>
            await BroadcastExcept(userId, new { Type = "UserOnline", Payload = new OnlineStatusPayload { UserId = userId } }));
    }

    /// <summary>
    /// Отправляет клиенту список его контактов с онлайн-статусами.
    /// </summary>
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

    /// <summary>
    /// Отдаёт публичный ключ пользователя.
    /// </summary>
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

    /// <summary>
    /// Отдаёт имя пользователя.
    /// </summary>
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

    /// <summary>
    /// Сохраняет сообщение и пересылает получателю (если он онлайн).
    /// </summary>
    static async Task ProcessSendMessage(BaseMessage msg, int senderId, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<SendMessagePayload>();
        if (p == null) return;

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
        // Автоматически добавляем получателя в контакты
        db.AddContact(senderId, p.ReceiverId);

        // Получатель не в сети — уведомляем отправителя
        if (!clients.TryGetValue(p.ReceiverId, out var recvWs) || recvWs.State != WebSocketState.Open)
        {
            await Send(ws, new { Type = "DeliveryStatus", Payload = new DeliveryStatusPayload { MessageId = p.MessageId, Status = "UserOffline" } });
            return;
        }

        // Пересылаем получателю
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

    /// <summary>
    /// Обрабатывает подтверждение доставки и уведомляет отправителя.
    /// </summary>
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

    /// <summary>
    /// Отдаёт историю переписки с указанным контактом.
    /// </summary>
    static async Task ProcessGetHistory(BaseMessage msg, int userId, WebSocket ws)
    {
        var p = msg.Payload.Deserialize<GetHistoryRequestPayload>();
        if (p == null) return;
        var messages = db.GetHistory(userId, p.ContactId);
        await Send(ws, new { Type = "HistoryResponse", Payload = new HistoryResponsePayload { ContactId = p.ContactId, Messages = messages } });
    }

    /// <summary>
    /// Добавляет контакт в обе стороны и сообщает статус.
    /// </summary>
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

    /// <summary>
    /// Обновляет имя пользователя и рассылает уведомление всем клиентам.
    /// </summary>
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

    /// <summary>
    /// Рассылает сообщение всем, кроме указанного клиента.
    /// </summary>
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

    /// <summary>
    /// Сериализует объект в JSON и отправляет как текстовый WebSocket-фрейм.
    /// Отправка сериализуется семафором сокета (SendAsync нельзя вызывать параллельно).
    /// </summary>
    static async Task Send(WebSocket ws, object obj)
    {
        if (ws.State != WebSocketState.Open) return;

        if (socketLocks.TryGetValue(ws, out var sem))
        {
            await sem.WaitAsync();
            try
            {
                // Повторная проверка после семафора — сокет мог закрыться
                if (ws.State != WebSocketState.Open) return;
                string json = JsonSerializer.Serialize(obj);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                sem.Release();
            }
        }
    }
}