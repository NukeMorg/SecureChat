using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using SecureChat.Core;
using Serilog;

namespace SecureChat.Client.Services;

/// <summary>
/// Транспортный слой клиента на SignalR HubConnection.
/// Каждый метод протокола — типизированный вызов хаб-метода; входящие события
/// пробрасываются наружу без изменений, поэтому окна не зависят от транспорта.
/// </summary>
public class NetworkService
{
    private HubConnection? _hub;
    private HelloPayload? _lastHello;
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
    public event Action<string>? OnBanned;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    // ---------- Подключение ----------

    public async Task ConnectAsync(string serverUrl)
    {
        if (IsConnected) return;

        Disconnect();
        string hubUrl = NormalizeHubUrl(serverUrl);

        try
        {
            Log.Information("Connecting to {Url}", hubUrl);
            _hub = new HubConnectionBuilder()
                .WithUrl(hubUrl)
                .WithAutomaticReconnect(new[]
                {
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
                    TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)
                })
                .Build();

            RegisterHandlers();
            _hub.Reconnected += OnHubReconnected;
            _hub.Closed += OnHubClosed;

            await _hub.StartAsync();
            Log.Information("Connected to {Url}", hubUrl);
            OnConnected?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect to {Url}", hubUrl);
            OnError?.Invoke($"Ошибка подключения к {hubUrl}: {ex.Message}");
            Disconnect();
            throw;
        }
    }

    public void Disconnect()
    {
        var hub = _hub;
        _hub = null;
        _lastHello = null;
        if (hub == null) return;

        bool wasActive = hub.State != HubConnectionState.Disconnected;
        try
        {
            hub.Closed -= OnHubClosed;
            hub.Reconnected -= OnHubReconnected;
        }
        catch { }

        Log.Information("Disconnecting from server");
        _ = Task.Run(async () => { try { await hub.DisposeAsync(); } catch { } });
        if (wasActive) OnDisconnected?.Invoke();
    }

    // ---------- Хаб-методы: аутентификация ----------

    public Task LoginAsync(LoginPayload payload) => InvokeAsync("Login", payload);

    public Task SendEmailCodeAsync(SendEmailCodePayload payload) => InvokeAsync("SendEmailCode", payload);

    public Task RegisterAccountAsync(RegisterAccountPayload payload) => InvokeAsync("RegisterAccount", payload);

    public Task HelloAsync(HelloPayload payload)
    {
        _lastHello = payload;
        return InvokeAsync("Hello", payload);
    }

    // ---------- Хаб-методы: чаты и контакты ----------

    public Task SendMessageAsync(SendMessagePayload payload) => InvokeAsync("SendMessage", payload);

    public Task AckAsync(AckPayload payload) => InvokeAsync("Ack", payload);

    public Task GetHistoryAsync(GetHistoryRequestPayload payload) => InvokeAsync("GetHistory", payload);

    public Task GetContactsAsync() => InvokeAsync("GetContacts");

    public Task GetPublicKeyAsync(GetPublicKeyPayload payload) => InvokeAsync("GetPublicKey", payload);

    public Task GetUserInfoAsync(GetUserInfoPayload payload) => InvokeAsync("GetUserInfo", payload);

    public Task AddContactAsync(AddContactPayload payload) => InvokeAsync("AddContact", payload);

    public Task UpdateProfileAsync(UpdateProfilePayload payload) => InvokeAsync("UpdateProfile", payload);

    // ---------- Администрирование (request/response через возвращаемое значение) ----------

    public Task<SimpleStatusPayload?> BanUserAsync(BanUserPayload payload) =>
        InvokeWithResultAsync("BanUser", payload);

    public Task<SimpleStatusPayload?> UnbanUserAsync(UnbanUserPayload payload) =>
        InvokeWithResultAsync("UnbanUser", payload);

    public Task<SimpleStatusPayload?> ChangeRoleAsync(ChangeRolePayload payload) =>
        InvokeWithResultAsync("ChangeRole", payload);

    public Task<SimpleStatusPayload?> ChangePasswordAsync(ChangePasswordPayload payload) =>
        InvokeWithResultAsync("ChangePassword", payload);

    /// <summary>Вызов хаб-метода с ответом-статусом (Admin-операции, смена пароля).</summary>
    private async Task<SimpleStatusPayload?> InvokeWithResultAsync(string methodName, object payload)
    {
        var hub = _hub;
        if (hub == null || hub.State != HubConnectionState.Connected)
            return new SimpleStatusPayload { Status = "NotConnected" };

        await _sendLock.WaitAsync();
        try
        {
            return await hub.InvokeAsync<SimpleStatusPayload>(methodName, payload);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Send error {Method}", methodName);
            return new SimpleStatusPayload { Status = "Error" };
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Отзыв сессии на сервере (Logout). Сбрасывает сохранённый Hello.</summary>
    public async Task LogoutAsync()
    {
        _lastHello = null;
        await InvokeAsync("Logout");
    }

    /// <summary>Отключение с опциональным отзывом сессии перед закрытием соединения.</summary>
    public async Task DisconnectAsync(bool logout = false)
    {
        if (logout) await LogoutAsync();
        Disconnect();
    }

    // ---------- Внутреннее ----------

    private async Task InvokeAsync(string methodName, object? payload = null)
    {
        var hub = _hub;
        if (hub == null || hub.State != HubConnectionState.Connected)
        {
            OnError?.Invoke("Нет подключения к серверу");
            return;
        }

        await _sendLock.WaitAsync();
        try
        {
            if (payload == null)
                await hub.InvokeAsync(methodName);
            else
                await hub.InvokeAsync(methodName, payload);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Send error {Method}", methodName);
            OnError?.Invoke($"Ошибка отправки: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void RegisterHandlers()
    {
        var hub = _hub!;
        hub.On<SendEmailCodeResponsePayload>("SendEmailCodeResponse", p => OnSendEmailCodeResponse?.Invoke(p));
        hub.On<LoginResponsePayload>("LoginResponse", p => OnLoginResponse?.Invoke(p));
        hub.On<RegisterAccountResponsePayload>("RegisterAccountResponse", p => OnRegisterAccountResponse?.Invoke(p));
        hub.On<HelloResponsePayload>("HelloResponse", p => OnHelloResponse?.Invoke(p));
        hub.On<IncomingMessagePayload>("IncomingMessage", p => OnIncomingMessage?.Invoke(p));
        hub.On<DeliveryStatusPayload>("DeliveryStatus", p => OnDeliveryStatus?.Invoke(p));
        hub.On<PublicKeyResponsePayload>("PublicKeyResponse", p => OnPublicKeyResponse?.Invoke(p));
        hub.On<ContactListPayload>("ContactList", p => OnContactList?.Invoke(p));
        hub.On<UserProfileUpdatedPayload>("UserProfileUpdated", p => OnUserProfileUpdated?.Invoke(p));
        hub.On<UserInfoResponsePayload>("UserInfoResponse", p => OnUserInfoResponse?.Invoke(p));
        hub.On<HistoryResponsePayload>("HistoryResponse", p => OnHistoryResponse?.Invoke(p));
        hub.On<AddContactResponsePayload>("AddContactResponse", p => OnAddContactResponse?.Invoke(p));
        hub.On<OnlineStatusPayload>("UserOnline", p => OnUserOnline?.Invoke(p.UserId));
        hub.On<OnlineStatusPayload>("UserOffline", p => OnUserOffline?.Invoke(p.UserId));
        hub.On<SimpleStatusPayload>("Error", p => OnError?.Invoke(p?.Status ?? "UnknownError"));
        hub.On<BannedPayload>("Banned", p => OnBanned?.Invoke(p?.Reason ?? ""));
    }

    private async Task OnHubReconnected(string? connectionId)
    {
        Log.Information("Auto-reconnect completed connectionId={ConnectionId}", connectionId);
        var hub = _hub;
        if (hub != null && _lastHello != null)
        {
            try
            {
                // новое соединение не аутентифицировано — восстанавливаем сессию
                await hub.InvokeAsync("Hello", _lastHello);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to restore session after reconnect");
            }
        }
        OnConnected?.Invoke();
    }

    private Task OnHubClosed(Exception? error)
    {
        if (error != null) Log.Warning(error, "Connection closed with error");
        OnDisconnected?.Invoke();
        return Task.CompletedTask;
    }

    private static string NormalizeHubUrl(string serverUrl)
    {
        string url = serverUrl.Trim();
        if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) url = "http://" + url[5..];
        else if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[6..];
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;
        return url.TrimEnd('/') + "/hub";
    }
}
