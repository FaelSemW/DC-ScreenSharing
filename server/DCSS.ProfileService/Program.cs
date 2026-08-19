using DCSS.ProfileService.Middleware;
using DCSS.ProfileService.Services;

var builder = WebApplication.CreateBuilder(args);

// Dynamic port configuration for Railway / Cloud environments
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services.AddControllers();
builder.Services.AddSingleton<ProfileStoreService>();
builder.Services.AddSingleton<ClientEnrollmentService>();

var app = builder.Build();

app.UseMiddleware<AdminAuthMiddleware>();

app.UseRouting();
app.MapControllers();

app.Run();

// Make Program accessible to WebApplicationFactory in integration tests
public partial class Program { }
