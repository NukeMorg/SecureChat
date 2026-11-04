using SecureChat.Server;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();

builder.Host.UseSerilog((context, cfg) =>
    cfg.ReadFrom.Configuration(context.Configuration)
       .Enrich.FromLogContext());

builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<EmailService>();

builder.Services.AddSignalR(options =>
{
    // inline E2E-вложения: шифртекст файла + ключи + base64-накладные расходы
    options.MaximumReceiveMessageSize = 12 * 1024 * 1024;
});

string baseUrl = builder.Configuration["Server:BaseUrl"] ?? "http://*:9000";
builder.WebHost.UseUrls(baseUrl);

var app = builder.Build();

app.MapHub<ChatHub>("/hub");
app.MapGet("/health", () => Results.Text("Healthy", "text/plain"));
app.MapGet("/", () => "SecureChat Server is Running");

Log.Information("=== SecureChat Server запуск на {Machine} ===", Environment.MachineName);
app.Services.GetRequiredService<Database>().Initialize();

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Фатальная ошибка при запуске сервера");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
