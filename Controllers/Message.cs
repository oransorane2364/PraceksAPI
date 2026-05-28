using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Text.RegularExpressions;

namespace PraceksAPI.Controllers
{
    // ==================== ОСНОВНОЙ КОНТРОЛЛЕР ДЛЯ ОТПРАВКИ СООБЩЕНИЙ ====================
    [Route("api/[controller]")]
    [ApiController]
    public class Message : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<Message> _logger;

        // Хранилище текущего сообщения (в памяти)
        private static string _currentMessage = "Заглушка: API работает!";
        private static string _helloMessage = "Привет из API-заглушки!";

        public Message(IConfiguration configuration, ILogger<Message> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult GetMessage()
        {
            return Ok(new
            {
                message = _currentMessage,
                timestamp = DateTime.Now,
                status = "success"
            });
        }

        [HttpGet("hello")]
        public IActionResult GetHello()
        {
            return Ok(new { message = _helloMessage });
        }

        // Отправка сообщения от пользователя к пользователю (с сохранением в БД)
        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new { error = "Запрос не может быть пустым" });
                }

                if (string.IsNullOrWhiteSpace(request.MessageText))
                {
                    return BadRequest(new { error = "Текст сообщения не может быть пустым" });
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
                    return BadRequest(new { error = "Нельзя отправить сообщение самому себе" });
                }

                const string messageType = "1";

                // Сохраняем только в mes_his (без архива)
                long historyId = await SaveToMessageHistory(request.SenderName, request.ReceiverName, request.MessageText, messageType);

                _currentMessage = request.MessageText;

                return Ok(new
                {
                    success = true,
                    message = $"Сообщение от '{request.SenderName}' для '{request.ReceiverName}' успешно отправлено",
                    messageId = historyId,
                    messageType = messageType,
                    sender = request.SenderName,
                    receiver = request.ReceiverName,
                    messageText = request.MessageText,
                    sentAt = DateTime.Now
                });
            }
            catch (PostgresException ex)
            {
                _logger.LogError(ex, "Ошибка PostgreSQL при отправке сообщения");
                return StatusCode(500, new { error = "Ошибка базы данных", details = ex.MessageText });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при отправке сообщения");
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

        // Метод SaveToMessageArchive УДАЛЁН

        [HttpPut("hello")]
        public IActionResult UpdateHelloMessage([FromBody] HelloUpdateRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Text))
            {
                return BadRequest(new { error = "Текст сообщения не может быть пустым" });
            }

            _helloMessage = request.Text;

            return Ok(new
            {
                message = $"Приветствие обновлено на: '{request.Text}'",
                updatedAt = DateTime.Now,
                currentHelloMessage = _helloMessage
            });
        }

        [HttpDelete("reset")]
        public IActionResult ResetMessages()
        {
            _currentMessage = "Заглушка: API работает!";
            _helloMessage = "Привет из API-заглушки!";

            return Ok(new
            {
                message = "Все сообщения сброшены к значениям по умолчанию",
                timestamp = DateTime.Now
            });
        }

        [HttpGet("all")]
        public IActionResult GetAllMessages()
        {
            return Ok(new
            {
                mainMessage = _currentMessage,
                helloMessage = _helloMessage,
                lastUpdate = DateTime.Now
            });
        }

        // GET: api/message/history/all - получить историю из БД
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
                    WHERE message_type = '1'
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
    }

    // ==================== КОНТРОЛЛЕР ДЛЯ ИСТОРИИ СООБЩЕНИЙ (mes_his) ====================
    [Route("api/message-history")]
    [ApiController]
    public class MessageHistoryController : ControllerBase
    {
        private readonly IConfiguration _configuration;

        public MessageHistoryController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        // GET: api/message-history/all - вся история из БД
        [HttpGet("all")]
        public async Task<IActionResult> GetAllHistory([FromQuery] int? limit = null, [FromQuery] string? messageType = null)
        {
            try
            {
                string? connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                string sql = @"
                    SELECT id, sender_name, recipient_name, message, message_type, created_at
                    FROM data.mes_his";

                if (!string.IsNullOrEmpty(messageType))
                {
                    sql += $" WHERE message_type = '{messageType}'";
                }
                else
                {
                    sql += " WHERE message_type = '1'";
                }

                sql += " ORDER BY created_at DESC";

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
                    totalMessages = messages.Count,
                    messages = messages,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET: api/message-history/user/{userName}/sent - отправленные пользователем
        [HttpGet("user/{userName}/sent")]
        public async Task<IActionResult> GetSentMessages(string userName, [FromQuery] int? limit = null)
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
                    WHERE sender_name = @userName AND message_type = '1'
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
                    type = "sent",
                    totalMessages = messages.Count,
                    messages = messages,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET: api/message-history/user/{userName}/received - полученные пользователем
        [HttpGet("user/{userName}/received")]
        public async Task<IActionResult> GetReceivedMessages(string userName, [FromQuery] int? limit = null)
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
                    WHERE recipient_name = @userName AND message_type = '1'
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
                    type = "received",
                    totalMessages = messages.Count,
                    messages = messages,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET: api/message-history/conversation - диалог между двумя пользователями
        [HttpGet("conversation")]
        public async Task<IActionResult> GetConversation([FromQuery] string user1, [FromQuery] string user2, [FromQuery] int? limit = null)
        {
            if (string.IsNullOrWhiteSpace(user1) || string.IsNullOrWhiteSpace(user2))
            {
                return BadRequest(new { error = "Имена обоих пользователей обязательны" });
            }

            try
            {
                string? connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                string sql = @"
                    SELECT id, sender_name, recipient_name, message, message_type, created_at
                    FROM data.mes_his 
                    WHERE ((sender_name = @user1 AND recipient_name = @user2) OR (sender_name = @user2 AND recipient_name = @user1))
                    AND message_type = '1'
                    ORDER BY created_at ASC";

                if (limit.HasValue && limit.Value > 0)
                    sql += $" LIMIT {limit.Value}";

                using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("@user1", user1);
                command.Parameters.AddWithValue("@user2", user2);
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
                    user1 = user1,
                    user2 = user2,
                    totalMessages = messages.Count,
                    conversation = messages,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    // ==================== МОДЕЛИ ДАННЫХ ====================

    public class SendMessageRequest
    {
        public string SenderName { get; set; } = string.Empty;
        public string ReceiverName { get; set; } = string.Empty;
        public string MessageText { get; set; } = string.Empty;
    }

    public class HelloUpdateRequest
    {
        public string Text { get; set; } = string.Empty;
    }
}