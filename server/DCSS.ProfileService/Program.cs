using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using DCSS.ProfileService.Middleware;
using DCSS.ProfileService.Services;

var builder = WebApplication.CreateBuilder(args);

// Dynamic port configuration for Railway / Cloud environments
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// Storage path configuration
var storagePath = builder.Configuration["ProfileService:StoragePath"] ?? 
                  Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage");
Directory.CreateDirectory(storagePath);

var keyringPath = Path.Combine(storagePath, "admin-keyring");
Directory.CreateDirectory(keyringPath);

// Persist ASP.NET Data Protection keys so container restarts never invalidate sessions
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyringPath))
    .SetApplicationName("DCSS.ProfileService.Admin");

// Cookie Authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "dcss_admin_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect("/admin");
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers();

// Core backend singleton services
builder.Services.AddSingleton<AuditLogService>();
builder.Services.AddSingleton<AccessKeyService>();
builder.Services.AddSingleton<CredentialSetService>();
builder.Services.AddSingleton<ProfileStoreService>();
builder.Services.AddSingleton<ClientEnrollmentService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<AdminAuthMiddleware>();

app.MapControllers();

// SPA fallback for /admin routes
app.MapFallbackToFile("/admin/{*path}", "admin/index.html");
app.MapFallbackToFile("/admin", "admin/index.html");

app.Run();

// Make Program accessible to WebApplicationFactory in integration tests
public partial class Program { }
