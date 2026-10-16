using AutoFill.Web;

EnvFile.Load(EnvFile.Find(Directory.GetCurrentDirectory()));

var builder = WebApplication.CreateBuilder(args);
// One JSON object per log line in the container (docker logs, log shippers); readable text when developing.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o => o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz ");
}
var settings = Settings.From(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddAutoFill(settings);

var app = builder.Build();
await app.MigrateDatabaseAsync();
app.UseAutoFill(settings);
app.Run();

/// <summary>Visible to the integration tests (WebApplicationFactory).</summary>
public partial class Program;
