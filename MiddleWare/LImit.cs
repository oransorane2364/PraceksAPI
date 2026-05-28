using StackExchange.Redis;
namespace PraceksAPI.MiddleWare
{
    public class RateLimitingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IConnectionMultiplexer _redis;
        private const int MaxRequests = 100;      
        private const int TimeWindowSeconds = 60; 

        public RateLimitingMiddleware(RequestDelegate next, IConnectionMultiplexer redis)
        {
            _next = next;
            _redis = redis;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var redisDb = _redis.GetDatabase();
            var key = $"rate_limit:{clientIp}";

            var currentCount = await redisDb.StringIncrementAsync(key);
            if (currentCount == 1)
                await redisDb.KeyExpireAsync(key, TimeSpan.FromSeconds(TimeWindowSeconds));

            if (currentCount > MaxRequests)
            {
                context.Response.StatusCode = 429; // Too Many Requests
                await context.Response.WriteAsync("Rate limit exceeded. Try again later.");
                return;
            }

            await _next(context);
        }
    }
}
