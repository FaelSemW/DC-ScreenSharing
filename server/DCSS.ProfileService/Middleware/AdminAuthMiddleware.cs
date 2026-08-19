namespace DCSS.ProfileService.Middleware;

public class AdminAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _adminApiKey;

    public AdminAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        var envKey = Environment.GetEnvironmentVariable("ADMIN_API_KEY");
        var configKey = config["ProfileService:AdminApiKey"];
        _adminApiKey = !string.IsNullOrEmpty(envKey) ? envKey : (!string.IsNullOrEmpty(configKey) ? configKey : "dev-admin-secret-key-replace-in-prod");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Only enforce authentication on administrative endpoints
        if (path.StartsWith("/api/v1/admin", StringComparison.OrdinalIgnoreCase))
        {
            var hasApiKey = context.Request.Headers.TryGetValue("X-Admin-Api-Key", out var headerKey);
            if (!hasApiKey || string.IsNullOrEmpty(headerKey))
            {
                // Also check Bearer Authorization header
                if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
                {
                    var token = authHeader.ToString().Replace("Bearer ", "").Trim();
                    if (string.Equals(token, _adminApiKey, StringComparison.Ordinal))
                    {
                        await _next(context);
                        return;
                    }
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\": \"Unauthorized. Missing or invalid X-Admin-Api-Key header.\"}");
                return;
            }

            if (!string.Equals(headerKey.ToString(), _adminApiKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\": \"Forbidden. Invalid admin credentials.\"}");
                return;
            }
        }

        await _next(context);
    }
}
