using Npgsql;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace PraceksAPI.Services
{
    public class ArchiveCleanupService : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<ArchiveCleanupService> _logger;
        private readonly IDistributedCache? _cache;  // ← Сделали nullable
        private readonly TimeSpan _interval = TimeSpan.FromHours(24);
        private readonly int _daysToArchive = 30;
        private readonly int _daysToDelete = 365;

        public ArchiveCleanupService(
            IConfiguration configuration,
            ILogger<ArchiveCleanupService> logger,
            IDistributedCache cache)  // ← Может быть null если Redis не работает
        {
            _configuration = configuration;
            _logger = logger;
            _cache = cache;  // ← Просто сохраняем, даже если null
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Сервис архивации и очистки запущен");

            // Ждём 5 минут перед стартом (дадим время Redis подняться)
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Начало выполнения задачи архивации и очистки");

                    // Проверяем Redis и получаем блокировку (если Redis работает)
                    bool canProceed = await TryAcquireLockAsync();
                    if (!canProceed)
                    {
                        _logger.LogInformation("Задача уже выполняется или Redis недоступен, пропускаем");
                        await Task.Delay(_interval, stoppingToken);
                        continue;
                    }

                    try
                    {
                        // 1. Перемещаем старые сообщения из mes_his в mes_arc
                        int archivedCount = await ArchiveOldMessages();

                        // 2. Удаляем очень старые сообщения из архива
                        int deletedCount = await DeleteOldArchivedMessages();

                        _logger.LogInformation($"Задача завершена. Архивировано: {archivedCount}, удалено: {deletedCount}");

                        // Сохраняем время последнего успешного запуска (если Redis работает)
                        await SaveLastRunTimeAsync();
                    }
                    finally
                    {
                        // Освобождаем блокировку
                        await ReleaseLockAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при выполнении задачи архивации и очистки");
                }

                // Ждём следующий запуск
                await Task.Delay(_interval, stoppingToken);
            }

            _logger.LogInformation("Сервис архивации и очистки остановлен");
        }

        // Безопасная попытка получить блокировку
        private async Task<bool> TryAcquireLockAsync()
        {
            if (_cache == null)
            {
                _logger.LogWarning("Redis недоступен, блокировка не используется");
                return true; // Если Redis нет - пропускаем проверку блокировки
            }

            try
            {
                string lockKey = "archive:cleanup_lock";
                string lockValue = Guid.NewGuid().ToString();

                var existingLock = await _cache.GetStringAsync(lockKey);
                if (!string.IsNullOrEmpty(existingLock))
                {
                    _logger.LogInformation("Блокировка уже существует, задача выполняется в другом экземпляре");
                    return false;
                }

                var lockOptions = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
                };

                await _cache.SetStringAsync(lockKey, lockValue, lockOptions);
                _logger.LogInformation("Блокировка получена");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось получить блокировку Redis, продолжаем без блокировки");
                return true; // Если Redis ошибся - всё равно работаем
            }
        }

        // Безопасное освобождение блокировки
        private async Task ReleaseLockAsync()
        {
            if (_cache == null) return;

            try
            {
                await _cache.RemoveAsync("archive:cleanup_lock");
                _logger.LogInformation("Блокировка освобождена");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось освободить блокировку Redis");
            }
        }

        // Сохраняем время последнего запуска
        private async Task SaveLastRunTimeAsync()
        {
            if (_cache == null) return;

            try
            {
                string lastRunKey = "archive:last_run";
                var lastRunData = JsonSerializer.Serialize(DateTime.Now);
                await _cache.SetStringAsync(lastRunKey, lastRunData, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7)
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось сохранить время последнего запуска в Redis");
            }
        }

        // Перемещение старых сообщений из mes_his в mes_arc
        private async Task<int> ArchiveOldMessages()
        {
            string? connectionString = _configuration.GetConnectionString("PostgresConnection");
            int archivedCount = 0;

            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            string selectSql = $@"
                SELECT id, sender_name, recipient_name, message, message_type, created_at
                FROM data.mes_his 
                WHERE created_at < NOW() - INTERVAL '{_daysToArchive} days'
                ORDER BY created_at ASC";

            var messagesToArchive = new List<ArchiveMessage>();
            using (var selectCommand = new NpgsqlCommand(selectSql, connection))
            using (var reader = await selectCommand.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    messagesToArchive.Add(new ArchiveMessage
                    {
                        Id = reader.GetInt64(0),
                        SenderName = reader.GetString(1),
                        RecipientName = reader.GetString(2),
                        Message = reader.GetString(3),
                        MessageType = reader.GetString(4),
                        CreatedAt = reader.GetDateTime(5)
                    });
                }
            }

            if (messagesToArchive.Count == 0)
            {
                _logger.LogInformation("Нет сообщений для архивации");
                return 0;
            }

            // Вставляем в архив
            foreach (var msg in messagesToArchive)
            {
                string insertSql = @"
                    INSERT INTO data.mes_arc (sender_name, recipient_name, message, message_type, created_at, archived_at)
                    VALUES (@senderName, @recipientName, @message, @messageType, @createdAt, @archivedAt)";

                using var insertCommand = new NpgsqlCommand(insertSql, connection);
                insertCommand.Parameters.AddWithValue("@senderName", msg.SenderName);
                insertCommand.Parameters.AddWithValue("@recipientName", msg.RecipientName);
                insertCommand.Parameters.AddWithValue("@message", msg.Message);
                insertCommand.Parameters.AddWithValue("@messageType", msg.MessageType);
                insertCommand.Parameters.AddWithValue("@createdAt", msg.CreatedAt);
                insertCommand.Parameters.AddWithValue("@archivedAt", DateTime.Now);

                await insertCommand.ExecuteNonQueryAsync();
                archivedCount++;
            }

            // Удаляем архивированные сообщения из mes_his
            string deleteSql = $@"
                DELETE FROM data.mes_his 
                WHERE created_at < NOW() - INTERVAL '{_daysToArchive} days'";

            using var deleteCommand = new NpgsqlCommand(deleteSql, connection);
            await deleteCommand.ExecuteNonQueryAsync();

            _logger.LogInformation($"Архивировано {archivedCount} сообщений");

            // Очищаем кэш (если Redis работает)
            if (_cache != null)
            {
                try
                {
                    await _cache.RemoveAsync("archive:pending_count");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось очистить кэш Redis");
                }
            }

            return archivedCount;
        }

        // Удаление очень старых сообщений из архива
        private async Task<int> DeleteOldArchivedMessages()
        {
            string? connectionString = _configuration.GetConnectionString("PostgresConnection");

            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            string deleteSql = $@"
                DELETE FROM data.mes_arc 
                WHERE created_at < NOW() - INTERVAL '{_daysToDelete} days'";

            using var command = new NpgsqlCommand(deleteSql, connection);
            int deletedCount = await command.ExecuteNonQueryAsync();

            _logger.LogInformation($"Удалено из архива {deletedCount} очень старых сообщений");

            return deletedCount;
        }

        private class ArchiveMessage
        {
            public long Id { get; set; }
            public string SenderName { get; set; } = string.Empty;
            public string RecipientName { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string MessageType { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
        }
    }
}