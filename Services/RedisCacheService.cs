using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using StackExchange.Redis;
namespace PraceksAPI.Services
{
    public class RedisCacheService : ICacheService
    {
        private readonly IDistributedCache _cache;
        private readonly ILogger<RedisCacheService> _logger;
        private readonly AsyncRetryPolicy _retryPolicy;

        public RedisCacheService(IDistributedCache cache, ILogger<RedisCacheService> logger)
        {
            _cache = cache;
            _logger = logger;

            // Polly: 3 попытки с задержкой 100, 200, 400 мс
            _retryPolicy = Policy
                .Handle<RedisConnectionException>()
                .Or<TimeoutException>()
                .Or<RedisException>()
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)),
                    onRetry: (exception, delay, retryCount, context) =>
                    {
                        _logger.LogWarning(exception,
                            "Redis retry {RetryCount}/3 after {Delay}ms, key: {Key}",
                            retryCount, delay.TotalMilliseconds, context?["Key"] ?? "unknown");
                    });
        }

        public async Task<string?> GetStringAsync(string key)
        {
            var context = new Polly.Context { ["Key"] = key };

            try
            {
                return await _retryPolicy.ExecuteAsync(async ctx =>
                {
                    return await _cache.GetStringAsync(key);
                }, context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redis failed after retries for key: {Key}", key);
                return null;
            }
        }

        public async Task SetStringAsync(string key, string value, TimeSpan? expiration = null)
        {
            var context = new Polly.Context { ["Key"] = key };

            try
            {
                await _retryPolicy.ExecuteAsync(async ctx =>
                {
                    var options = new DistributedCacheEntryOptions();
                    if (expiration.HasValue)
                        options.AbsoluteExpirationRelativeToNow = expiration;
                    else
                        options.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);

                    await _cache.SetStringAsync(key, value, options);
                }, context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set cache for key: {Key}", key);
            }
        }

        public async Task RemoveAsync(string key)
        {
            var context = new Polly.Context { ["Key"] = key };

            try
            {
                await _retryPolicy.ExecuteAsync(async ctx =>
                {
                    await _cache.RemoveAsync(key);
                }, context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove cache for key: {Key}", key);
            }
        }

        public async Task<T?> GetAsync<T>(string key) where T : class
        {
            var data = await GetStringAsync(key);
            if (string.IsNullOrEmpty(data))
                return null;

            try
            {
                return JsonSerializer.Deserialize<T>(data);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Deserialization error for key: {Key}", key);
                return null;
            }
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null) where T : class
        {
            try
            {
                var jsonData = JsonSerializer.Serialize(value);
                await SetStringAsync(key, jsonData, expiration);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Serialization error for key: {Key}", key);
            }
        }
    }
}

