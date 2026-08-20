using System.Security.Cryptography;
using System.Text;

namespace DCSS.ProfileService.Middleware;

public class AdminAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;

    public AdminAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _config = config;
    }

    private string GetAdminApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("ADMIN_API_KEY");
        var configKey = _config["ProfileService:AdminApiKey"];
        return !string.IsNullOrEmpty(envKey) ? envKey : (!string.IsNullOrEmpty(configKey) ? configKey : "dev-admin-secret-key-replace-in-prod");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Skip auth check for login and public endpoints
        if (path.Equals("/api/v1/admin/auth/login", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/admin/auth/session", StringComparison.OrdinalIgnoreCase) ||
            !path.StartsWith("/api/v1/admin", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // 1. Check if user is authenticated via Cookie Authentication (Web Admin)
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        var adminApiKey = GetAdminApiKey();

        // 2. Check for X-Admin-Api-Key Header or Bearer Token (Maintainer Desktop)
        var hasApiKey = context.Request.Headers.TryGetValue("X-Admin-Api-Key", out var headerKey);
        if (hasApiKey && !string.IsNullOrEmpty(headerKey))
        {
            if (FixedTimeEquals(headerKey.ToString(), adminApiKey))
            {
                await _next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\": \"Forbidden. Invalid admin credentials.\"}");
            return;
        }

        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var token = authHeader.ToString().Replace("Bearer ", "").Trim();
            if (FixedTimeEquals(token, adminApiKey))
            {
                await _next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\": \"Forbidden. Invalid admin credentials.\"}");
            return;
        }

        // Neither cookie nor valid header provided
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\": \"Unauthorized. Please sign in to access admin console.\"}");
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
