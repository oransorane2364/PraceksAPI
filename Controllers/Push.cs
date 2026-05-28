using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace PraceksAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class Push : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<Push> _logger;

        // Простое хранилище последнего уведомления (только для отладки)
        private static string _lastNotification = string.Empty;
        private static DateTime _lastNotificationTime;
        private static PushRecord? _lastPushRecord = null;

        public Push(IConfiguration configuration, ILogger<Push> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        // POST: api/push/send - отправить push-уведомление
        [HttpPost("send")]
        public async Task<IActionResult> SendNotification([FromBody] PushRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new { error = "Запрос не может быть пустым" });
                }

                if (string.IsNullOrWhiteSpace(request.Title))
                {
                    return BadRequest(new { error = "Title не может быть пустым" });
                }

                if (string.IsNullOrWhiteSpace(request.SenderName))
                {
                    return BadRequest(new { error = "Имя отправителя не может быть пустым" });
                }

                if (string.IsNullOrWhiteSpace(request.ReceiverName))
                {
                    return BadRequest(new { error = "Имя получателя не может быть пустым" });
                }

                if (request.SenderName.Equals(request.ReceiverName, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { error = "Нельзя отправить push-уведомление самому себе" });
                }

                const string messageType = "2";

                // Формируем текст сообщения для истории
                string messageText = $"[PUSH] {request.Title}: {request.Body ?? "без текста"}";

                if (request.Data != null)
                {
                    messageText += $" | Data: {System.Text.Json.JsonSerializer.Serialize(request.Data)}";
                }

                // Сохраняем последнее уведомление (для отладки)
                _lastNotification = messageText;
                _lastNotificationTime = DateTime.Now;
                _lastPushRecord = new PushRecord
                {
                    SenderName = request.SenderName,
                    ReceiverName = request.ReceiverName,
                    Title = request.Title,
                    Body = request.Body,
                    Data = request.Data,
                    SentAt = DateTime.Now
                };

                // Сохраняем только в mes_his (без архива)
                long historyId = await SaveToMessageHistory(request.SenderName, request.ReceiverName, messageText, messageType);

                return Ok(new
                {
                    success = true,
                    message = $"Push-уведомление отправлено от '{request.SenderName}' для '{request.ReceiverName}'",
                    notification = new
                    {
                        title = request.Title,
                        body = request.Body ?? string.Empty,
                        data = request.Data,
                        sender = request.SenderName,
                        receiver = request.ReceiverName
                    },
                    messageId = historyId,
                    messageType = messageType,
                    sentAt = DateTime.Now
                });
            }
            catch (PostgresException ex)
            {
                _logger.LogError(ex, "Ошибка PostgreSQL при отправке push");
                return StatusCode(500, new { error = "Ошибка базы данных", details = ex.MessageText });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при отправке push");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера", details = ex.Message });
            }
        }

        // Сохранение только в таблицу mes_his (без архива)
        private async Task<long> SaveToMessageHistory(string senderName, string recipientName, string messageText, string messageType)
        {
            string? connectionString = _configuration.GetConnectionString("PostgresConnection");

            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            string sql = @"
                INSERT INTO data.mes_his (sender_name, recipient_name, message, message_type, created_at)
                VALUES (@senderName, @recipientName, @message, @messageType, @createdAt)
                RETURNING id";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@senderName", senderName);
            command.Parameters.AddWithValue("@recipientName", recipientName);
            command.Parameters.AddWithValue("@message", messageText);
            command.Parameters.AddWithValue("@messageType", messageType);
            command.Parameters.AddWithValue("@createdAt", DateTime.Now);

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }

        

        // POST: api/push/send-bulk - массовая рассылка
        [HttpPost("send-bulk")]
        public async Task<IActionResult> SendBulkNotifications([FromBody] BulkPushRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Title))
                {
                    return BadRequest(new { error = "Title не может быть пустым" });
                }

                if (string.IsNullOrWhiteSpace(request.SenderName))
                {
                    return BadRequest(new { error = "Имя отправителя не может быть пустым" });
                }

                if (request.Receivers == null || request.Receivers.Length == 0)
                {
                    return BadRequest(new { error = "Список получателей не может быть пустым" });
                }

                const string messageType = "2";
                var sentMessages = new List<object>();
                var messageIds = new List<long>();
                var errors = new List<string>();

                foreach (var receiver in request.Receivers)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(receiver))
                        {
                            errors.Add("Пропущен: пустое имя получателя");
                            continue;
                        }

                        if (receiver.Equals(request.SenderName, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"Пропущен: нельзя отправить самому себе ({receiver})");
                            continue;
                        }

                        string messageText = $"[PUSH] {request.Title}: {request.Body ?? "без текста"}";

                        if (request.Data != null)
                        {
                            messageText += $" | Data: {System.Text.Json.JsonSerializer.Serialize(request.Data)}";
                        }

                        // Сохраняем только в mes_his (без архива)
                        long historyId = await SaveToMessageHistory(request.SenderName, receiver, messageText, messageType);
                        messageIds.Add(historyId);

                        sentMessages.Add(new
                        {
                            receiver = receiver,
                            status = "sent",
                            messageId = historyId
                        });
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Ошибка для {receiver}: {ex.Message}");
                        _logger.LogError(ex, $"Ошибка при отправке push для {receiver}");
                    }
                }

                return Ok(new
                {
                    success = true,
                    message = $"Массовая рассылка push-уведомлений от '{request.SenderName}' выполнена",
                    totalSent = sentMessages.Count,
                    totalErrors = errors.Count,
                    receivers = sentMessages,
                    messageIds = messageIds,
                    errors = errors.Any() ? errors : null,
                    sentAt = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при массовой рассылке push");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера", details = ex.Message });
            }
        }

        // GET: api/push/history/all - получить все push из истории БД
        [HttpGet("history/all")]
        public async Task<IActionResult> GetAllPushHistory([FromQuery] int? limit = null)
        {
            try
            {
                string? connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                string sql = @"
                    SELECT id, sender_name, recipient_name, message, message_type, created_at
                    FROM data.mes_his 
                    WHERE message_type = '2'
                    ORDER BY created_at DESC";

                if (limit.HasValue && limit.Value > 0)
                    sql += $" LIMIT {limit.Value}";

                using var command = new NpgsqlCommand(sql, connection);
                using var reader = await command.ExecuteReaderAsync();

                var messages = new List<object>();
                while (await reader.ReadAsync())
                {
                    messages.Add(new
                    {
                        id = reader.GetInt64(0),
                        senderName = reader.GetString(1),
                        recipientName = reader.GetString(2),
                        message = reader.GetString(3),
                        messageType = reader.GetString(4),
                        createdAt = reader.GetDateTime(5)
                    });
                }

                return Ok(new
                {
                    totalPushes = messages.Count,
                    pushes = messages,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET: api/push/user/{userName}/sent - отправленные пользователем
        [HttpGet("user/{userName}/sent")]
        public async Task<IActionResult> GetSentPushNotifications(string userName, [FromQuery] int? limit = null)
        {
            if (string.IsNullOrWhiteSpace(userName))
            {
                return BadRequest(new { error = "Имя пользователя не может быть пустым" });
            }

            try
            {
                string? connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                string sql = @"
                    SELECT id, sender_name, recipient_name, message, message_type, created_at
                    FROM data.mes_his 
                    WHERE sender_name = @userName AND message_type = '2'
                    ORDER BY created_at DESC";

                if (limit.HasValue && limit.Value > 0)
                    sql += $" LIMIT {limit.Value}";

                using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("@userName", userName);
                using var reader = await command.ExecuteReaderAsync();

                var messages = new List<object>();
                while (await reader.ReadAsync())
                {
                    messages.Add(new
                    {
                        id = reader.GetInt64(0),
                        senderName = reader.GetString(1),
                        recipientName = reader.GetString(2),
                        message = reader.GetString(3),
                        messageType = reader.GetString(4),
                        createdAt = reader.GetDateTime(5)
                    });
                }

                return Ok(new
                {
                    userName = userName,
                    type = "sent pushes",
                    totalPushes = messages.Count,
                    pushNotifications = messages,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET: api/push/user/{userName}/received - полученные пользователем
        [HttpGet("user/{userName}/received")]
        public async Task<IActionResult> GetReceivedPushNotifications(string userName, [FromQuery] int? limit = null)
        {
            if (string.IsNullOrWhiteSpace(userName))
            {
                return BadRequest(new { error = "Имя пользователя не может быть пустым" });
            }

            try
            {
                string? connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                string sql = @"
                    SELECT id, sender_name, recipient_name, message, message_type, created_at
                    FROM data.mes_his 
                    WHERE recipient_name = @userName AND message_type = '2'
                    ORDER BY created_at DESC";

                if (limit.HasValue && limit.Value > 0)
                    sql += $" LIMIT {limit.Value}";

                using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("@userName", userName);
                using var reader = await command.ExecuteReaderAsync();

                var messages = new List<object>();
                while (await reader.ReadAsync())
                {
                    messages.Add(new
                    {
                        id = reader.GetInt64(0),
                        senderName = reader.GetString(1),
                        recipientName = reader.GetString(2),
                        message = reader.GetString(3),
                        messageType = reader.GetString(4),
                        createdAt = reader.GetDateTime(5)
                    });
                }

                return Ok(new
                {
                    userName = userName,
                    type = "received pushes",
                    totalPushes = messages.Count,
                    pushNotifications = messages,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET: api/push/last - последнее уведомление
        [HttpGet("last")]
        public IActionResult GetLastNotification()
        {
            if (string.IsNullOrEmpty(_lastNotification))
            {
                return Ok(new
                {
                    hasNotifications = false,
                    message = "Push-уведомлений пока не отправлялось"
                });
            }

            return Ok(new
            {
                hasNotifications = true,
                lastNotification = _lastNotification,
                lastSentAt = _lastNotificationTime,
                lastPush = _lastPushRecord,
                message = "Последнее push-уведомление получено"
            });
        }

        // GET: api/push/ping
        [HttpGet("ping")]
        public IActionResult Ping()
        {
            return Ok(new
            {
                status = " Push-сервис работает (с сохранением в PostgreSQL)",
                timestamp = DateTime.Now,
                database = "PostgreSQL",
                messageType = "2",
                endpoints = new
                {
                    send = "POST api/push/send - отправить push",
                    sendBulk = "POST api/push/send-bulk - массовая рассылка",
                    historyAll = "GET api/push/history/all - все push из БД",
                    userSent = "GET api/push/user/{userName}/sent - отправленные",
                    userReceived = "GET api/push/user/{userName}/received - полученные",
                    last = "GET api/push/last - последнее уведомление",
                    clear = "DELETE api/push/clear - очистить локальную историю"
                }
            });
        }

        // DELETE: api/push/clear - очистить локальную историю
        [HttpDelete("clear")]
        public IActionResult ClearHistory()
        {
            _lastNotification = string.Empty;
            _lastPushRecord = null;
            return Ok(new
            {
                message = "Локальная история push-уведомлений очищена",
                note = "Данные в БД (mes_his) не затронуты"
            });
        }
    }

    // ==================== МОДЕЛИ ====================

    public class PushRequest
    {
        public string SenderName { get; set; } = string.Empty;
        public string ReceiverName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Body { get; set; }
        public object? Data { get; set; }
    }

    public class BulkPushRequest
    {
        public string SenderName { get; set; } = string.Empty;
        public string[] Receivers { get; set; } = Array.Empty<string>();
        public string Title { get; set; } = string.Empty;
        public string? Body { get; set; }
        public object? Data { get; set; }
    }

    public class PushRecord
    {
        public string SenderName { get; set; } = string.Empty;
        public string ReceiverName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Body { get; set; }
        public object? Data { get; set; }
        public DateTime SentAt { get; set; }
    }
}