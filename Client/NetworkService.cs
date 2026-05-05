using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SecureChat.Common;

namespace SecureChat.Client;

public class NetworkService
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private volatile bool _isDisconnecting;

    public event Action<IncomingMessagePayload>? OnIncomingMessage;
    public event Action<DeliveryStatusPayload>? OnDeliveryStatus;
    public event Action<PublicKeyResponsePayload>? OnPublicKeyResponse;
    public event Action<UserListPayload>? OnUserList;
    public event Action<UserProfileUpdatedPayload>? OnUserProfileUpdated;
    public event Action<UserInfoResponsePayload>? OnUserInfoResponse;
    public event Action<HistoryResponsePayload>? OnHistoryResponse;
    public event Action<ContactListPayload>? OnContactList;
    public event Action<string>? OnError;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<int>? OnRegisterResponse;

    public bool IsConnected => _tcp?.Connected ?? false;

    public async Task ConnectAsync(string host, int port)
    {
        _isDisconnecting = false;
        try
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(host, port);
            _stream = _tcp.GetStream();
            _reader = new StreamReader(_stream);
            _writer = new StreamWriter(_stream) { AutoFlush = true };
            OnConnected?.Invoke();
            _ = Task.Run(ReadLoop);
        }
        catch (SocketException ex)
        {
            OnError?.Invoke($"Ошибка подключения: {ex.Message}");
            Disconnect();
            throw;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Неизвестная ошибка подключения: {ex.Message}");
            Disconnect();
            throw;
        }
    }

    public async Task SendAsync(object message)
    {
        if (_stream == null || !_tcp!.Connected)
        {
            if (!_isDisconnecting) OnError?.Invoke("Нет подключения к серверу");
            return;
        }
        try
        {
            string json = JsonSerializer.Serialize(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            byte[] len = BitConverter.GetBytes(bytes.Length);
            await _stream.WriteAsync(len, 0, 4);
            await _stream.WriteAsync(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            if (!_isDisconnecting) OnError?.Invoke($"Ошибка отправки: {ex.Message}");
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (_tcp!.Connected)
            {
                byte[] lenBuf = new byte[4];
                int read = await _stream!.ReadAsync(lenBuf, 0, 4);
                if (read < 4) break;
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len <= 0 || len > 100 * 1024 * 1024) break;

                char[] buf = new char[len];
                int total = 0;
                while (total < len)
                {
                    int r = await _reader!.ReadAsync(buf, total, len - total);
                    if (r == 0) break;
                    total += r;
                }
                string json = new(buf, 0, total);
                var msg = JsonSerializer.Deserialize<BaseMessage>(json);
                if (msg == null) continue;

                switch (msg.Type)
                {
                    case "IncomingMessage":
                        OnIncomingMessage?.Invoke(msg.Payload.Deserialize<IncomingMessagePayload>());
                        break;
                    case "DeliveryStatus":
                        OnDeliveryStatus?.Invoke(msg.Payload.Deserialize<DeliveryStatusPayload>());
                        break;
                    case "PublicKeyResponse":
                        OnPublicKeyResponse?.Invoke(msg.Payload.Deserialize<PublicKeyResponsePayload>());
                        break;
                    case "UserList":
                        OnUserList?.Invoke(msg.Payload.Deserialize<UserListPayload>());
                        break;
                    case "UserProfileUpdated":
                        OnUserProfileUpdated?.Invoke(msg.Payload.Deserialize<UserProfileUpdatedPayload>());
                        break;
                    case "UserInfoResponse":
                        OnUserInfoResponse?.Invoke(msg.Payload.Deserialize<UserInfoResponsePayload>());
                        break;
                    case "HistoryResponse":
                        OnHistoryResponse?.Invoke(msg.Payload.Deserialize<HistoryResponsePayload>());
                        break;
                    case "ContactList":
                        OnContactList?.Invoke(msg.Payload.Deserialize<ContactListPayload>());
                        break;
                    case "RegisterResponse":
                        var reg = msg.Payload.Deserialize<RegisterResponsePayload>();
                        OnRegisterResponse?.Invoke(reg.AssignedId);
                        break;
                    case "Error":
                        var err = msg.Payload.Deserialize<SimpleStatusPayload>();
                        OnError?.Invoke(err.Status);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (!_isDisconnecting) OnError?.Invoke($"Ошибка чтения данных: {ex.Message}");
        }
        finally
        {
            OnDisconnected?.Invoke();
            if (!_isDisconnecting) Disconnect();
        }
    }

    public void Disconnect()
    {
        _isDisconnecting = true;
        try { _writer?.Close(); } catch { }
        try { _reader?.Close(); } catch { }
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
    }
}