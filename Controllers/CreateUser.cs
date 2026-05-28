using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace PraceksAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UserController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<UserController> _logger;

        public UserController(IConfiguration configuration, ILogger<UserController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        // POST: api/user/createuser
        [HttpPost("createuser")]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
        {
            try
            {
                // Проверка входных данных
                if (request == null)
                {
                    return BadRequest(new { error = "Неверные данные запроса" });
                }

                if (string.IsNullOrWhiteSpace(request.Username))
                {
                    return BadRequest(new { error = "Имя пользователя обязательно" });
                }

                if (string.IsNullOrWhiteSpace(request.Password))
                {
                    return BadRequest(new { error = "Пароль обязателен" });
                }

                if (string.IsNullOrWhiteSpace(request.Email))
                {
                    return BadRequest(new { error = "Email обязателен" });
                }

                // Валидация email
                if (!IsValidEmail(request.Email))
                {
                    return BadRequest(new { error = "Некорректный формат email" });
                }

                // Строка подключения к PostgreSQL
                var connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();

                // 1. Проверяем, существует ли пользователь с таким username или email (исправлено: data.user_id)
                string checkQuery = "SELECT COUNT(*) FROM data.user_id WHERE username = @username OR email = @email";
                using var checkCmd = new NpgsqlCommand(checkQuery, conn);
                checkCmd.Parameters.AddWithValue("@username", request.Username);
                checkCmd.Parameters.AddWithValue("@email", request.Email);

                long count = (long)await checkCmd.ExecuteScalarAsync();

                if (count > 0)
                {
                    _logger.LogWarning($"Попытка создания существующего пользователя: {request.Username}, {request.Email}");
                    return Conflict(new
                    {
                        error = "Пользователь уже существует",
                        details = "Пользователь с таким именем или email уже зарегистрирован"
                    });
                }

                // 2. Создаем нового пользователя (исправлено: data.user_id)
                string insertQuery = @"
                    INSERT INTO data.user_id (username, password, email) 
                    VALUES (@username, @password, @email) 
                    RETURNING id, username, email";

                using var insertCmd = new NpgsqlCommand(insertQuery, conn);
                insertCmd.Parameters.AddWithValue("@username", request.Username);
                insertCmd.Parameters.AddWithValue("@password", request.Password); // TODO: Добавить хеширование
                insertCmd.Parameters.AddWithValue("@email", request.Email);

                using var reader = await insertCmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    var newUser = new
                    {
                        Id = reader.GetInt32(0),
                        Username = reader.GetString(1),
                        Email = reader.GetString(2)
                    };

                    _logger.LogInformation($"Создан новый пользователь: {request.Username} с ID {newUser.Id}");

                    return Ok(new
                    {
                        success = true,
                        message = "Пользователь успешно создан",
                        user = newUser,
                        createdAt = DateTime.Now
                    });
                }

                return StatusCode(500, new { error = "Не удалось создать пользователя" });
            }
            catch (PostgresException ex)
            {
                _logger.LogError(ex, "Ошибка PostgreSQL при создании пользователя");

                // Обработка специфических ошибок PostgreSQL
                if (ex.SqlState == "23505") // Unique violation
                {
                    return Conflict(new { error = "Пользователь с таким именем или email уже существует" });
                }

                return StatusCode(500, new { error = "Ошибка базы данных", details = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при создании пользователя");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера", details = ex.Message });
            }
        }

        // GET: api/user/check - проверить существование пользователя
        [HttpGet("check")]
        public async Task<IActionResult> CheckUserExists([FromQuery] string? username, [FromQuery] string? email)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(email))
                {
                    return BadRequest(new { error = "Укажите username или email для проверки" });
                }

                var connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();

                // Исправлено: data.user_id
                string query = "SELECT COUNT(*) FROM data.user_id WHERE username = @username OR email = @email";
                using var cmd = new NpgsqlCommand(query, conn);
                cmd.Parameters.AddWithValue("@username", username ?? "");
                cmd.Parameters.AddWithValue("@email", email ?? "");

                long count = (long)await cmd.ExecuteScalarAsync();

                return Ok(new
                {
                    exists = count > 0,
                    message = count > 0 ? "Пользователь существует" : "Пользователь не найден",
                    checkedBy = new
                    {
                        username = !string.IsNullOrWhiteSpace(username) ? username : null,
                        email = !string.IsNullOrWhiteSpace(email) ? email : null
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при проверке пользователя");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера", details = ex.Message });
            }
        }

        // GET: api/user/ping - проверка работоспособности
        [HttpGet("ping")]
        public IActionResult Ping()
        {
            var hasDbConfig = !string.IsNullOrEmpty(_configuration.GetConnectionString("PostgresConnection"));

            return Ok(new
            {
                status = "✅ User-сервис работает",
                timestamp = DateTime.Now,
                isConfigured = hasDbConfig,
                endpoints = new
                {
                    create = "POST /api/user/createuser",
                    check = "GET /api/user/check?username=...&email=...",
                    ping = "GET /api/user/ping"
                }
            });
        }

        // GET: api/user/getuser - получить пользователя по username или email
        [HttpGet("getuser")]
        public async Task<IActionResult> GetUser([FromQuery] string? username, [FromQuery] string? email)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(email))
                {
                    return BadRequest(new { error = "Укажите username или email" });
                }

                var connectionString = _configuration.GetConnectionString("PostgresConnection");

                using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();

                // Исправлено: data.user_id
                string query = @"
                    SELECT id, username, email, created_at 
                    FROM data.user_id 
                    WHERE username = @username OR email = @email";

                using var cmd = new NpgsqlCommand(query, conn);
                cmd.Parameters.AddWithValue("@username", username ?? "");
                cmd.Parameters.AddWithValue("@email", email ?? "");

                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    return Ok(new
                    {
                        id = reader.GetInt32(0),
                        username = reader.GetString(1),
                        email = reader.GetString(2),
                        createdAt = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3)
                    });
                }

                return NotFound(new { error = "Пользователь не найден" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при получении пользователя");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера", details = ex.Message });
            }
        }

        // Вспомогательный метод для валидации email
        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }
    }

    // Модель запроса для создания пользователя
    public class CreateUserRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}