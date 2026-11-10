using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SecureChat.Core;
using Serilog;

namespace SecureChat.Client.Services;

/// <summary>Сессия, сохраняемая в config.json для автоматического входа.</summary>
public record AutoLoginSession(int UserId, string UserName, string Token, string PublicKey, string PrivateKey, string Role);

/// <summary>Сохранённые учётные данные для предзаполнения формы входа.</summary>
public sealed record SavedCredentials(string PublicKeyPem, string PrivateKeyPem, bool RememberMe, string Login);

/// <summary>
/// Работа с %APPDATA%\SecureChat\config.json: предзаполнение формы входа,
/// сохранение сессии (токен и приватный ключ шифруются через DPAPI) и
/// автоматический вход по сохранённому токену (Hello-рукопожатие).
/// </summary>
public static class ConfigService
{
    public const string DefaultServerUrl = "ws://localhost:9000/";

    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(ConfigService));

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecureChat");

    private static string ConfigFile => Path.Combine(ConfigDir, "config.json");

    /// <summary>Читает сохранённые ключи и предзаполнение формы входа; null — файла нет.</summary>
    public static SavedCredentials? ReadSavedCredentials()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return null;
            var cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ConfigFile));
            if (cfg == null) return null;

            cfg.TryGetValue("PublicKey", out var pub);
            string? priv = null;
            if (cfg.TryGetValue("PrivateKey", out var privEnc))
                priv = SecretProtector.Unprotect(privEnc);
            bool remember = cfg.TryGetValue("RememberMe", out var rem) && rem == "true";
            cfg.TryGetValue("Login", out var login);

            return new SavedCredentials(pub ?? "", priv ?? "", remember, login ?? "");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read config.json");
            return null;
        }
    }

    /// <summary>Сохраняет сессию пользователя. При rememberMe=false (в т.ч. после регистрации) автологин отключён.</summary>
    public static void SaveSession(AutoLoginSession session, bool rememberMe, string? login)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var cfg = new Dictionary<string, string>
            {
                ["ClientId"] = session.UserId.ToString(),
                ["UserName"] = session.UserName,
                // секреты шифруются через DPAPI (CurrentUser)
                ["Token"] = SecretProtector.Protect(session.Token),
                ["PublicKey"] = session.PublicKey,
                ["PrivateKey"] = SecretProtector.Protect(session.PrivateKey),
                ["Theme"] = App.CurrentTheme,
                ["RememberMe"] = rememberMe ? "true" : "false"
            };
            if (rememberMe && !string.IsNullOrWhiteSpace(login))
                cfg["Login"] = login;

            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(cfg));
            Log.Debug("config.json saved");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save config.json");
        }
    }

    /// <summary>Отключает автологин, не трогая сохранённые ключи.</summary>
    public static void ClearAutoLoginState()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;
            var cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ConfigFile));
            if (cfg == null) return;
            cfg["RememberMe"] = "false";
            cfg.Remove("Login");
            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(cfg));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clear auto-login");
        }
    }

    /// <summary>Читает сохранённую сессию для автологина; null — автологин невозможен.</summary>
    public static AutoLoginSession? ReadAutoLoginConfig()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return null;
            var cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ConfigFile));
            if (cfg == null) return null;
            if (!cfg.TryGetValue("RememberMe", out var rem) || rem != "true") return null;
            if (!cfg.TryGetValue("Token", out var tokenRaw) || string.IsNullOrEmpty(tokenRaw)) return null;
            if (!cfg.TryGetValue("PublicKey", out var pub) || string.IsNullOrEmpty(pub)) return null;
            if (!cfg.TryGetValue("PrivateKey", out var privRaw) || string.IsNullOrEmpty(privRaw)) return null;
            if (!cfg.TryGetValue("ClientId", out var idStr) || !int.TryParse(idStr, out var id)) return null;
            if (!cfg.TryGetValue("UserName", out var name) || string.IsNullOrEmpty(name)) return null;

            string? token = SecretProtector.Unprotect(tokenRaw);
            string? privateKey = SecretProtector.Unprotect(privRaw);
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(privateKey)) return null;

            return new AutoLoginSession(id, name, token, pub, privateKey, "Employee");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read auto-login");
            return null;
        }
    }

    /// <summary>Блокирующая попытка автологина по сохранённому токену (Hello-рукопожатие) с таймаутом.</summary>
    public static AutoLoginSession? TryAutoLoginBlocking(NetworkService network, AutoLoginSession saved, int timeoutMs)
    {
        try
        {
            return Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                try
                {
                    await network.ConnectAsync(DefaultServerUrl).WaitAsync(cts.Token);

                    var tcs = new TaskCompletionSource<HelloResponsePayload>();
                    void handler(HelloResponsePayload p) => tcs.TrySetResult(p);
                    network.OnHelloResponse += handler;
                    try
                    {
                        await network.HelloAsync(new HelloPayload { ClientId = saved.UserId, Token = saved.Token });

                        var resp = await tcs.Task.WaitAsync(cts.Token);
                        if (resp == null || resp.Status != "Ok") return null;

                        return new AutoLoginSession(
                            resp.UserId,
                            string.IsNullOrEmpty(resp.UserName) ? saved.UserName : resp.UserName,
                            saved.Token, saved.PublicKey, saved.PrivateKey,
                            string.IsNullOrEmpty(resp.Role) ? "Employee" : resp.Role);
                    }
                    finally { network.OnHelloResponse -= handler; }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "TryAutoLoginBlocking failed");
                    try { network.Disconnect(); } catch { }
                    return null;
                }
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TryAutoLoginBlocking outer error");
            try { network.Disconnect(); } catch { }
            return null;
        }
    }
}
