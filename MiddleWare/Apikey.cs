using Microsoft.Extensions.Configuration;

namespace PraceksAPI.MiddleWare
{
    public class ApiKeyAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IConfiguration _configuration;
        private const string ApiKeyHeader = "X-API-Key";

        public ApiKeyAuthMiddleware(RequestDelegate next, IConfiguration configuration)
        {
            _next = next;
            _configuration = configuration;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Пропускаем health check без авторизации
            if (context.Request.Path == "/health" || context.Request.Path == "/healthz")
            {
                await _next(context);
                return;
            }

            // Берём ключ из конфига (appsettings.json или переменных окружения)
            var validApiKey = _configuration["ApiSettings:ApiKey"] ?? "647543";

            if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var apiKey) || apiKey != validApiKey)
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";

                var errorResponse = new
                {
                    error = "Unauthorized",
                    message = "Invalid or missing API Key",
                    statusCode = 401
                };

                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(errorResponse));
                return;
            }

            await _next(context);
        }
    }
}
