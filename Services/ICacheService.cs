using System;
using System.Threading.Tasks;
namespace PraceksAPI.Services 
{
    public interface ICacheService
    {
        Task<string?> GetStringAsync(string key);
        Task SetStringAsync(string key, string value, TimeSpan? expiration = null);
        Task RemoveAsync(string key);
        Task<T?> GetAsync<T>(string key) where T : class;
        Task SetAsync<T>(string key, T value, TimeSpan? expiration = null) where T : class;
    }
}
