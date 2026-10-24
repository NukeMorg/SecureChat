using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SecureChat.Common;

namespace SecureChat.Client.Services;

// Сетевое взаимодействие через WebSocket (ClientWebSocket) с поддержкой авторизации
public class NetworkService
{
    private ClientWebSocket? _ws;              // Активное WS-соединение
    private CancellationTokenSource? _cts;     // Отмена фоновых операций
    private volatile bool _isDisconnecting;    // Флаг, чтобы не сыпать ошибками при штатном отключении

    // События входящих сообщений от сервера
    public event Action<IncomingMessagePayload>? OnIncomingMessage;
    public event Action<DeliveryStatusPayload>? OnDeliveryStatus;
    public event Action<PublicKeyResponsePayload>? OnPublicKeyResponse;
    public event Action<UserProfileUpdatedPayload>? OnUserProfileUpdated;
    public event Action<UserInfoResponsePayload>? OnUserInfoResponse;
    public event Action<HistoryResponsePayload>? OnHistoryResponse;
    public event Action<ContactListPayload>? OnContactList;
    public event Action<string>? OnError;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<LoginResponsePayload>? OnLoginResponse;
    public event Action<RegisterAccountResponsePayload>? OnRegisterAccountResponse;
    public event Action<HelloResponsePayload>? OnHelloResponse;
    public event Action<AddContactResponsePayload>? OnAddContactResponse;
    public event Action<int>? OnUserOnline;    // Пользователь вошёл в сеть
    public event Action<int>? OnUserOffline;   // Пользователь вышел из сети

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    // Установка соединения с сервером
    public async Task ConnectAsync(string serverUrl)
    {
        Disconnect();                         // Закрываем предыдущее соединение
        _isDisconnecting = false;
        _cts = new CancellationTokenSource();

        // Нормализация URL: префикс ws:// и завершающий слэш
        string normalizedUrl = serverUrl.Trim();
        if (!normalizedUrl.StartsWith("ws://") && !normalizedUrl.StartsWith("wss://"))
        {
            normalizedUrl = "ws://" + normalizedUrl;
        }
        if (!normalizedUrl.EndsWith("/"))
        {
            normalizedUrl += "/";
        }

        try
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(normalizedUrl), _cts.Token);
            OnConnected?.Invoke();
            _ = Task.Run(ReadLoop);           // Запуск фонового чтения
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Ошибка подключения к {normalizedUrl}: {ex.Message}");
            Disconnect();
            throw;
        }
    }

    // Отправка сообщения в формате JSON
    public async Task SendAsync(object message)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
        {
            if (!_isDisconnecting) OnError?.Invoke("Нет подключения к серверу");
            return;
        }
        try
        {
            string json = JsonSerializer.Serialize(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (!_isDisconnecting) OnError?.Invoke($"Ошибка отправки: {ex.Message}");
        }
    }

    // Фоновый цикл приёма сообщений
    private async Task ReadLoop()
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (_ws?.State == WebSocketState.Open && !_cts!.Token.IsCancellationRequested)
            {
                // Накопление фрагментированного сообщения
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                // Десериализация базового конверта
                string json = Encoding.UTF8.GetString(ms.ToArray());
                var msg = JsonSerializer.Deserialize<BaseMessage>(json);
                if (msg == null) continue;

                // Маршрутизация по типу сообщения
                switch (msg.Type)
                {
                    case "LoginResponse":
                        OnLoginResponse?.Invoke(msg.Payload.Deserialize<LoginResponsePayload>()!);
                        break;
                    case "RegisterAccountResponse":
                        OnRegisterAccountResponse?.Invoke(msg.Payload.Deserialize<RegisterAccountResponsePayload>()!);
                        break;
                    case "HelloResponse":
                        OnHelloResponse?.Invoke(msg.Payload.Deserialize<HelloResponsePayload>()!);
                        break;
                    case "IncomingMessage":
                        OnIncomingMessage?.Invoke(msg.Payload.Deserialize<IncomingMessagePayload>()!);
                        break;
                    case "DeliveryStatus":
                        OnDeliveryStatus?.Invoke(msg.Payload.Deserialize<DeliveryStatusPayload>()!);
                        break;
                    case "PublicKeyResponse":
                        OnPublicKeyResponse?.Invoke(msg.Payload.Deserialize<PublicKeyResponsePayload>()!);
                        break;
                    case "ContactList":
                        OnContactList?.Invoke(msg.Payload.Deserialize<ContactListPayload>()!);
                        break;
                    case "UserProfileUpdated":
                        OnUserProfileUpdated?.Invoke(msg.Payload.Deserialize<UserProfileUpdatedPayload>()!);
                        break;
                    case "UserInfoResponse":
                        OnUserInfoResponse?.Invoke(msg.Payload.Deserialize<UserInfoResponsePayload>()!);
                        break;
                    case "HistoryResponse":
                        OnHistoryResponse?.Invoke(msg.Payload.Deserialize<HistoryResponsePayload>()!);
                        break;
                    case "AddContactResponse":
                        OnAddContactResponse?.Invoke(msg.Payload.Deserialize<AddContactResponsePayload>()!);
                        break;
                    case "UserOnline":
                        var online = msg.Payload.Deserialize<OnlineStatusPayload>();
                        if (online != null) OnUserOnline?.Invoke(online.UserId);
                        break;
                    case "UserOffline":
                        var offline = msg.Payload.Deserialize<OnlineStatusPayload>();
                        if (offline != null) OnUserOffline?.Invoke(offline.UserId);
                        break;
                    case "Error":
                        var err = msg.Payload.Deserialize<SimpleStatusPayload>();
                        OnError?.Invoke(err?.Status ?? "UnknownError");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }   // Штатная отмена
        catch (Exception ex)
        {
            if (!_isDisconnecting) OnError?.Invoke($"Ошибка сети WebSocket: {ex.Message}");
        }
        finally
        {
            OnDisconnected?.Invoke();
            if (!_isDisconnecting) Disconnect();  // Автоотключение при разрыве
        }
    }

    // Принудительное закрытие соединения
    public void Disconnect()
    {
        _isDisconnecting = true;
        _cts?.Cancel();
        try { _ws?.Dispose(); } catch { }
        _ws = null;
    }
}