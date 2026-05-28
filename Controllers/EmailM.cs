using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace PraceksAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EmailM : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailM> _logger;
        private static readonly List<EmailStubRecord> _sentEmailsStub = new();

        public EmailM(IConfiguration configuration, ILogger<EmailM> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        // POST: api/emailm/send
        [HttpPost("send")]
        public async Task<IActionResult> SendEmail([FromBody] EmailRequest request)
        {
            try
            {
                if (request == null)
                    return BadRequest(new { error = "Запрос не может быть пустым" });

                if (string.IsNullOrWhiteSpace(request.SenderName))
                    return BadRequest(new { error = "Имя отправителя не может быть пустым" });

                if (string.IsNullOrWhiteSpace(request.RecipientName))
                    return BadRequest(new { error = "Имя получателя не может быть пустым" });

                if (request.SenderName.Equals(request.RecipientName, StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { error = "Нельзя отправить email самому себе" });

                if (string.IsNullOrWhiteSpace(request.Subject) && string.IsNullOrWhiteSpace(request.Body))
                    return BadRequest(new { error = "Укажите тему или текст письма" });

                const string messageType = "3";

                // Получаем email из БД
                string? recipientEmail = await GetUserEmailFromDb(request.RecipientName);

                if (string.IsNullOrEmpty(recipientEmail))
                {
                    return BadRequest(new
                    {
                        error = $"Пользователь '{request.RecipientName}' не найден или у него не указан email"
                    });
                }

                string messageText = $"[EMAIL] Для: {request.RecipientName} ({recipientEmail})\n" +
                                    $"Тема: {request.Subject ?? "Без темы"}\n" +
                                    $"Текст: {request.Body ?? request.PlainTextBody ?? "без текста"}";

                // Сохраняем только в mes_his
                long historyId = await SaveToMessageHistory(request.SenderName, request.RecipientName, messageText, messageType);

                _sentEmailsStub.Add(new EmailStubRecord
                {
                    Id = _sentEmailsStub.Count + 1,
                    From = request.SenderName,
                    To = request.RecipientName,
                    ToEmail = recipientEmail,
                    Subject = request.Subject ?? "Без темы",
                    Body = request.Body ?? request.PlainTextBody ?? "без текста",
                    SentAt = DateTime.Now
                });

                return Ok(new
                {
                    success = true,
                    message = $"Email сохранён в БД от '{request.SenderName}' для '{request.RecipientName}'",
                    to = recipientEmail,
                    sender = request.SenderName,
                    recipient = request.RecipientName,
                    subject = request.Subject,
                    messageId = historyId,
                    sentAt = DateTime.Now
                });
            }
            catch (PostgresException ex)
            {
                return StatusCode(500, new { error = "Ошибка базы данных", details = ex.MessageText });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Внутренняя ошибка сервера", details = ex.Message });
            }
        }

        // POST: api/emailm/send-bulk - массовая рассылка
        [HttpPost("send-bulk")]
        public async Task<IActionResult> SendBulkEmails([FromBody] BulkEmailRequest request)
        {
            try
            {
                if (request == null)
                    return BadRequest(new { error = "Запрос не может быть пустым" });

                if (string.IsNullOrWhiteSpace(request.SenderName))
                    return BadRequest(new { error = "Имя отправителя не может быть пустым" });

                if (request.RecipientNames == null || request.RecipientNames.Length == 0)
                    return BadRequest(new { error = "Список получателей не может быть пустым" });

                if (string.IsNullOrWhiteSpace(request.Subject) && string.IsNullOrWhiteSpace(request.Body))
                    return BadRequest(new { error = "Укажите тему или текст письма" });

                const string messageType = "3";
                var sentEmails = new List<object>();
                var messageIds = new List<long>();
                var errors = new List<string>();

                foreach (var recipientName in request.RecipientNames)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(recipientName))
                        {
                            errors.Add("Пропущен: пустое имя получателя");
                            continue;
                        }

                        // Получаем email из БД
                        string? recipientEmail = await GetUserEmailFromDb(recipientName);

                        if (string.IsNullOrEmpty(recipientEmail))
                        {
                            errors.Add($"Пользователь '{recipientName}' не найден или у него не указан email");
                            continue;
                        }

                        string messageText = $"[EMAIL] Для: {recipientName} ({recipientEmail})\n" +
                                            $"Тема: {request.Subject ?? "Без темы"}\n" +
                                            $"Текст: {request.Body ?? "без текста"}";

                        
                        long historyId = await SaveToMessageHistory(request.SenderName, recipientName, messageText, messageType);
                        messageIds.Add(historyId);

                        _sentEmailsStub.Add(new EmailStubRecord
                        {
                            Id = _sentEmailsStub.Count + 1,
                            From = request.SenderName,
                            To = recipientName,
                            ToEmail = recipientEmail,
                            Subject = request.Subject ?? "Без темы",
                            Body = request.Body ?? "без текста",
                            SentAt = DateTime.Now
                        });

                        sentEmails.Add(new
                        {
                            recipientName = recipientName,
                            email = recipientEmail,
                            status = "sent",
                            messageId = historyId
                        });
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Ошибка для {recipientName}: {ex.Message}");
                        _logger.LogError(ex, $"Ошибка при отправке email для {recipientName}");
                    }
                }

                return Ok(new
                {
                    success = true,
                    message = $"Массовая рассылка email от '{request.SenderName}' выполнена",
                    totalSent = sentEmails.Count,
                    totalErrors = errors.Count,
                    sentEmails = sentEmails,
                    messageIds = messageIds,
                    errors = errors.Any() ? errors : null,
                    sentAt = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при массовой рассылке");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера", details = ex.Message });
            }
        }

        
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

        

        // Получение email из таблицы data.user_id по имени пользователя
        [HttpGet("user/{userName}/email")]
        public async Task<IActionResult> GetUserEmailByName(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
            {
                return BadRequest(new { error = "Имя пользователя не может быть пустым" });
            }

            var email = await GetUserEmailFromDb(userName);

            if (string.IsNullOrEmpty(email))
            {
                return NotFound(new
                {
                    error = $"Пользователь '{userName}' не найден или у него не указан email",
                    userName = userName
                });
            }

            return Ok(new
            {
                userName = userName,
                email = email,
                timestamp = DateTime.Now
            });
        }

        private async Task<string?> GetUserEmailFromDb(string userName)
        {
            try
            {
                string? connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                string sql = "SELECT email FROM data.user_id WHERE username = @userName";
                using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("@userName", userName);

                var result = await command.ExecuteScalarAsync();
                return result?.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при получении email для {userName}");
                return null;
            }
        }

        // GET: api/emailm/debug/users - список всех пользователей
        [HttpGet("debug/users")]
        public async Task<IActionResult> GetAllUsers()
        {
            try
            {
                string? connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                string sql = "SELECT id, username, email FROM data.user_id ORDER BY id";
                using var command = new NpgsqlCommand(sql, connection);
                using var reader = await command.ExecuteReaderAsync();

                var users = new List<object>();
                while (await reader.ReadAsync())
                {
                    users.Add(new
                    {
                        id = reader.GetInt64(0),
                        username = reader.GetString(1),
                        email = reader.GetString(2)
                    });
                }

                return Ok(new { totalUsers = users.Count, users = users, timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET: api/emailm/debug/check-user/{username} - проверить пользователя
        [HttpGet("debug/check-user/{username}")]
        public async Task<IActionResult> CheckUser(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return BadRequest(new { error = "Имя пользователя не может быть пустым" });
            }

            try
            {
                string? connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                string sql = "SELECT id, username, email FROM data.user_id WHERE username = @username";
                using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("@username", username);

                using var reader = await command.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    return Ok(new
                    {
                        exists = true,
                        id = reader.GetInt64(0),
                        username = reader.GetString(1),
                        email = reader.GetString(2),
                        message = $"Пользователь '{username}' найден"
                    });
                }
                else
                {
                    return NotFound(new
                    {
                        exists = false,
                        username = username,
                        message = $"Пользователь '{username}' не найден"
                    });
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET: api/emailm/history/all - получить все сообщения из mes_his
        [HttpGet("history/all")]
        public async Task<IActionResult> GetAllHistoryFromDb([FromQuery] int? limit = null)
        {
            try
            {
                string? connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                string sql = @"
                    SELECT id, sender_name, recipient_name, message, message_type, created_at
                    FROM data.mes_his 
                    WHERE message_type = '3'
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

                return Ok(new { totalMessages = messages.Count, messages = messages, timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET: api/emailm/stub-list - список заглушек
        [HttpGet("stub-list")]
        public IActionResult GetStubEmails([FromQuery] int? limit = null)
        {
            var emails = _sentEmailsStub.OrderByDescending(e => e.SentAt).ToList();

            if (limit.HasValue && limit.Value > 0)
            {
                emails = emails.Take(limit.Value).ToList();
            }

            return Ok(new
            {
                totalStubEmails = _sentEmailsStub.Count,
                emails = emails,
                timestamp = DateTime.Now
            });
        }

        // DELETE: api/emailm/stub-clear - очистить заглушки
        [HttpDelete("stub-clear")]
        public IActionResult ClearStubEmails()
        {
            int clearedCount = _sentEmailsStub.Count;
            _sentEmailsStub.Clear();

            return Ok(new
            {
                success = true,
                message = $"Очищено {clearedCount} записей заглушек",
                clearedCount = clearedCount,
                timestamp = DateTime.Now
            });
        }

        // GET: api/emailm/ping
        [HttpGet("ping")]
        public IActionResult Ping()
        {
            return Ok(new
            {
                status = "✅ Email-сервис работает",
                timestamp = DateTime.Now,
                tables = new[] { "data.mes_his", "data.user_id" },
                endpoints = new
                {
                    send = "POST /api/emailm/send",
                    sendBulk = "POST /api/emailm/send-bulk",
                    getUserEmail = "GET /api/emailm/user/{userName}/email",
                    debugUsers = "GET /api/emailm/debug/users",
                    debugCheckUser = "GET /api/emailm/debug/check-user/{username}",
                    historyAll = "GET /api/emailm/history/all",
                    stubList = "GET /api/emailm/stub-list",
                    stubClear = "DELETE /api/emailm/stub-clear"
                }
            });
        }
    }

    

    public class EmailRequest
    {
        public string SenderName { get; set; } = string.Empty;
        public string RecipientName { get; set; } = string.Empty;
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? PlainTextBody { get; set; }
    }

    public class BulkEmailRequest
    {
        public string SenderName { get; set; } = string.Empty;
        public string[] RecipientNames { get; set; } = Array.Empty<string>();
        public string? Subject { get; set; }
        public string? Body { get; set; }
    }

    public class EmailStubRecord
    {
        public int Id { get; set; }
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string ToEmail { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime SentAt { get; set; }
    }
}