using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SecureChat.Core;
using Serilog;

namespace SecureChat.Client.Services;

public class NetworkService
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _isDisconnecting;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private static readonly ILogger Log = Serilog.Log.ForContext<NetworkService>();

    public event Action<SendEmailCodeResponsePayload>? OnSendEmailCodeResponse;
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
    public event Action<int>? OnUserOnline;
    public event Action<int>? OnUserOffline;

    public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;

    public async Task ConnectAsync(string serverUrl)
    {
        if (IsConnected) return;

        Disconnect();
        _isDisconnecting = false;
        _cts = new CancellationTokenSource();

        string normalizedUrl = serverUrl.Trim();
        if (!normalizedUrl.StartsWith("ws://") && !normalizedUrl.StartsWith("wss://"))
            normalizedUrl = "ws://" + normalizedUrl;
        if (!normalizedUrl.EndsWith("/"))
            normalizedUrl += "/";

        try
        {
            Log.Information("Подключение к {Url}", normalizedUrl);
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(normalizedUrl), _cts.Token);
            Log.Information("Соединение установлено {Url}", normalizedUrl);
            OnConnected?.Invoke();
            _ = Task.Run(ReadLoop);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка подключения к {Url}", normalizedUrl);
            OnError?.Invoke($"Ошибка подключения к {normalizedUrl}: {ex.Message}");
            Disconnect();
            throw;
        }
    }

    public async Task SendAsync(object message)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
        {
            if (!_isDisconnecting) OnError?.Invoke("Нет подключения к серверу");
            return;
        }

        await _sendLock.WaitAsync();
        try
        {
            if (_ws.State != WebSocketState.Open) return;
            string json = JsonSerializer.Serialize(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (!_isDisconnecting) Log.Error(ex, "Ошибка отправки сообщения");
            if (!_isDisconnecting) OnError?.Invoke($"Ошибка отправки: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoop()
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (_ws != null && _ws.State == WebSocketState.Open && !_cts!.Token.IsCancellationRequested)
            {
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

                string json = Encoding.UTF8.GetString(ms.ToArray());
                var msg = JsonSerializer.Deserialize<BaseMessage>(json);
                if (msg == null) continue;

                switch (msg.Type)
                {
                    case "SendEmailCodeResponse":
                        OnSendEmailCodeResponse?.Invoke(msg.Payload.Deserialize<SendEmailCodeResponsePayload>()!);
                        break;
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
        catch (OperationCanceledException) { Log.Debug("ReadLoop отменён"); }
        catch (Exception ex)
        {
            if (!_isDisconnecting) Log.Error(ex, "Ошибка сети WebSocket");
            if (!_isDisconnecting) OnError?.Invoke($"Ошибка сети WebSocket: {ex.Message}");
        }
        finally
        {
            Log.Debug("ReadLoop завершён");
            OnDisconnected?.Invoke();
        }
    }

    public void Disconnect()
    {
        Log.Information("Отключение от сервера");
        _isDisconnecting = true;
        _cts?.Cancel();
        try { _ws?.Dispose(); } catch { }
        _ws = null;
    }
}