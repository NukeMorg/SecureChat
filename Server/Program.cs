using SecureChat.Server;
using SecureChat.Server.Auth;
using Serilog;

namespace SecureChat.Server;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Configuration.AddUserSecrets<Program>(optional: true);
        builder.Configuration.AddEnvironmentVariables();

        builder.Host.UseSerilog((context, cfg) =>
            cfg.ReadFrom.Configuration(context.Configuration)
               .Enrich.FromLogContext());

        builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

        builder.Services.AddSingleton<ConnectionRegistry>();
        builder.Services.AddSingleton<IDatabase, Database>();
        builder.Services.AddSingleton<IEmailService, EmailService>();
        builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
        builder.Services.AddSingleton<ISessionRepository>(sp =>
            new SessionRepository(
                sp.GetRequiredService<IDatabase>().ConnectionString,
                sp.GetRequiredService<IJwtTokenService>()));

        builder.Services.AddSignalR(options =>
        {
            // inline E2E-вложения: 10 МБ файл + base64-накладные расходы и метаданные
            options.MaximumReceiveMessageSize = 20 * 1024 * 1024;
        });

        string baseUrl = builder.Configuration["Server:BaseUrl"] ?? "http://*:9000";
        builder.WebHost.UseUrls(baseUrl);

        var app = builder.Build();

        app.MapHub<ChatHub>("/hub");
        app.MapGet("/health", () => Results.Text("Healthy", "text/plain"));
        app.MapGet("/", () => "SecureChat Server is Running");

        Log.Information("=== SecureChat Server started on {Machine} ===", Environment.MachineName);
        app.Services.GetRequiredService<IDatabase>().Initialize();
        app.Services.GetRequiredService<ISessionRepository>().Initialize();

        try
        {
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error while running the server");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
