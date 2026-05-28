using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace PraceksAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ArchiveManagerController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<ArchiveManagerController> _logger;

        public ArchiveManagerController(IConfiguration configuration, ILogger<ArchiveManagerController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        // POST: api/archivemanager/archive - ручной запуск архивации
        [HttpPost("archive")]
        public async Task<IActionResult> ManualArchive([FromBody] ArchiveRequest? request)
        {
            try
            {
                int daysToArchive = request?.DaysToArchive ?? 30;
                int daysToDelete = request?.DaysToDelete ?? 365;

                if (daysToArchive < 1 || daysToDelete < 1)
                {
                    return BadRequest(new { error = "Количество дней должно быть больше 0" });
                }

                var result = await RunArchiveProcess(daysToArchive, daysToDelete);

                return Ok(new
                {
                    success = true,
                    message = "Архивация и очистка выполнены",
                    archivedCount = result.archivedCount,
                    deletedFromArchiveCount = result.deletedCount,
                    daysToArchive = daysToArchive,
                    daysToDelete = daysToDelete,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при ручной архивации");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера", details = ex.Message });
            }
        }

        // GET: api/archivemanager/stats - статистика по хранению
        [HttpGet("stats")]
        public async Task<IActionResult> GetStorageStats()
        {
            try
            {
                string? connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                // Статистика по mes_his
                string mainStatsSql = @"
                    SELECT 
                        COUNT(*) as total,
                        COUNT(CASE WHEN message_type = '1' THEN 1 END) as type1_count,
                        COUNT(CASE WHEN message_type = '2' THEN 1 END) as type2_count,
                        COUNT(CASE WHEN message_type = '3' THEN 1 END) as type3_count,
                        MIN(created_at) as oldest,
                        MAX(created_at) as newest,
                        COUNT(CASE WHEN created_at < NOW() - INTERVAL '30 days' THEN 1 END) as older_than_30days
                    FROM data.mes_his";

                long mainTotal = 0;
                long mainType1 = 0;
                long mainType2 = 0;
                long mainType3 = 0;
                DateTime? mainOldest = null;
                DateTime? mainNewest = null;
                long mainOlderThan30 = 0;

                using (var command = new NpgsqlCommand(mainStatsSql, connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        mainTotal = reader.GetInt64(0);
                        mainType1 = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                        mainType2 = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                        mainType3 = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                        mainOldest = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
                        mainNewest = reader.IsDBNull(5) ? null : reader.GetDateTime(5);
                        mainOlderThan30 = reader.IsDBNull(6) ? 0 : reader.GetInt64(6);
                    }
                }

                // Статистика по mes_arc
                string archiveStatsSql = @"
                    SELECT 
                        COUNT(*) as total,
                        MIN(created_at) as oldest,
                        MAX(created_at) as newest,
                        COUNT(CASE WHEN created_at < NOW() - INTERVAL '365 days' THEN 1 END) as older_than_365days
                    FROM data.mes_arc";

                long archiveTotal = 0;
                DateTime? archiveOldest = null;
                DateTime? archiveNewest = null;
                long archiveOlderThan365 = 0;

                using (var command = new NpgsqlCommand(archiveStatsSql, connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        archiveTotal = reader.GetInt64(0);
                        archiveOldest = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
                        archiveNewest = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
                        archiveOlderThan365 = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                    }
                }

                return Ok(new
                {
                    mainTable = new
                    {
                        totalMessages = mainTotal,
                        byType = new { type1 = mainType1, type2 = mainType2, type3 = mainType3 },
                        oldestMessage = mainOldest,
                        newestMessage = mainNewest,
                        messagesOlderThan30Days = mainOlderThan30,
                        willBeArchived = mainOlderThan30
                    },
                    archiveTable = new
                    {
                        totalMessages = archiveTotal,
                        oldestMessage = archiveOldest,
                        newestMessage = archiveNewest,
                        messagesOlderThan365Days = archiveOlderThan365,
                        willBeDeleted = archiveOlderThan365
                    },
                    settings = new
                    {
                        archiveAfterDays = 30,
                        deleteAfterDays = 365,
                        nextAutoRun = "Every day at 00:00"
                    },
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // DELETE: api/archivemanager/cleanup/old - принудительная очистка
        [HttpDelete("cleanup/old")]
        public async Task<IActionResult> ForceCleanup([FromQuery] int daysToDelete = 365)
        {
            try
            {
                if (daysToDelete < 1)
                {
                    return BadRequest(new { error = "Количество дней должно быть больше 0" });
                }

                string? connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                string deleteSql = @"
                    DELETE FROM data.mes_arc 
                    WHERE created_at < NOW() - INTERVAL '@days days'
                    RETURNING id";

                deleteSql = deleteSql.Replace("@days", daysToDelete.ToString());

                var deletedIds = new List<long>();
                using var command = new NpgsqlCommand(deleteSql, connection);
                using var reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    deletedIds.Add(reader.GetInt64(0));
                }

                return Ok(new
                {
                    success = true,
                    message = $"Принудительная очистка выполнена",
                    deletedCount = deletedIds.Count,
                    deletedIds = deletedIds.Take(100), // Показываем первые 100
                    daysToDelete = daysToDelete,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // POST: api/archivemanager/restore - восстановление из архива
        [HttpPost("restore")]
        public async Task<IActionResult> RestoreFromArchive([FromBody] RestoreRequest request)
        {
            try
            {
                if (request.MessageIds == null || request.MessageIds.Length == 0)
                {
                    return BadRequest(new { error = "Список ID сообщений не может быть пустым" });
                }

                string? connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                int restoredCount = 0;
                var restoredIds = new List<long>();

                foreach (var id in request.MessageIds)
                {
                    // Получаем сообщение из архива
                    string selectSql = @"
                        SELECT sender_name, recipient_name, message, message_type, created_at
                        FROM data.mes_arc 
                        WHERE id = @id";

                    using var selectCommand = new NpgsqlCommand(selectSql, connection);
                    selectCommand.Parameters.AddWithValue("@id", id);
                    using var reader = await selectCommand.ExecuteReaderAsync();

                    if (!await reader.ReadAsync())
                    {
                        continue;
                    }

                    string senderName = reader.GetString(0);
                    string recipientName = reader.GetString(1);
                    string message = reader.GetString(2);
                    string messageType = reader.GetString(3);
                    DateTime createdAt = reader.GetDateTime(4);

                    await reader.CloseAsync();

                    // Восстанавливаем в mes_his
                    string insertSql = @"
                        INSERT INTO data.mes_his (sender_name, recipient_name, message, message_type, created_at)
                        VALUES (@senderName, @recipientName, @message, @messageType, @createdAt)
                        RETURNING id";

                    using var insertCommand = new NpgsqlCommand(insertSql, connection);
                    insertCommand.Parameters.AddWithValue("@senderName", senderName);
                    insertCommand.Parameters.AddWithValue("@recipientName", recipientName);
                    insertCommand.Parameters.AddWithValue("@message", message);
                    insertCommand.Parameters.AddWithValue("@messageType", messageType);
                    insertCommand.Parameters.AddWithValue("@createdAt", createdAt);

                    long newId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync());
                    restoredIds.Add(newId);

                    // Удаляем из архива
                    string deleteSql = "DELETE FROM data.mes_arc WHERE id = @id";
                    using var deleteCommand = new NpgsqlCommand(deleteSql, connection);
                    deleteCommand.Parameters.AddWithValue("@id", id);
                    await deleteCommand.ExecuteNonQueryAsync();

                    restoredCount++;
                }

                return Ok(new
                {
                    success = true,
                    message = $"Восстановлено {restoredCount} сообщений из архива",
                    restoredCount = restoredCount,
                    newMessageIds = restoredIds,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Вспомогательный метод для архивации
        private async Task<(int archivedCount, int deletedCount)> RunArchiveProcess(int daysToArchive, int daysToDelete)
        {
            string? connectionString = _configuration.GetConnectionString("PostgresConnection");
            int archivedCount = 0;
            int deletedCount = 0;

            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Получаем сообщения для архивации
            string selectSql = $@"
                SELECT id, sender_name, recipient_name, message, message_type, created_at
                FROM data.mes_his 
                WHERE created_at < NOW() - INTERVAL '{daysToArchive} days'
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

            // Архивируем
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

            // Удаляем из mes_his
            if (archivedCount > 0)
            {
                string deleteSql = $@"
                    DELETE FROM data.mes_his 
                    WHERE created_at < NOW() - INTERVAL '{daysToArchive} days'";
                using var deleteCommand = new NpgsqlCommand(deleteSql, connection);
                await deleteCommand.ExecuteNonQueryAsync();
            }

            // Удаляем старые из архива
            string deleteArchiveSql = $@"
                DELETE FROM data.mes_arc 
                WHERE created_at < NOW() - INTERVAL '{daysToDelete} days'";
            using var deleteArchiveCommand = new NpgsqlCommand(deleteArchiveSql, connection);
            deletedCount = await deleteArchiveCommand.ExecuteNonQueryAsync();

            return (archivedCount, deletedCount);
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

    // Модели запросов
    public class ArchiveRequest
    {
        public int DaysToArchive { get; set; } = 30;
        public int DaysToDelete { get; set; } = 365;
    }

    public class RestoreRequest
    {
        public long[] MessageIds { get; set; } = Array.Empty<long>();
    }
}

