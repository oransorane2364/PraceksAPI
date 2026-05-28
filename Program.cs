using PraceksAPI.Services;
using StackExchange.Redis;
using Polly;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Добавление контроллеров
builder.Services.AddControllers();

// Настройка Swagger с поддержкой API Key
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "API Key необходим для доступа к эндпоинтам. Введите: 647543",
        Name = "X-API-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "ApiKeyScheme"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            new List<string>()
        }
    });
});

// Регистрация фонового сервиса архивации
builder.Services.AddHostedService<ArchiveCleanupService>();

// НАСТРОЙКА REDIS - читаем из переменных окружения или конфигурации
var redisConnectionString = builder.Configuration["Redis__ConnectionString"]
                            ?? builder.Configuration.GetConnectionString("Redis")
                            ?? Environment.GetEnvironmentVariable("REDIS_CONNECTION")
                            ?? "localhost:6379";

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnectionString;
    options.InstanceName = "PraceksAPI_";

    options.ConfigurationOptions = new ConfigurationOptions
    {
        EndPoints = { redisConnectionString },
        ConnectTimeout = 5000,
        SyncTimeout = 5000,
        AbortOnConnectFail = false,
        ReconnectRetryPolicy = new LinearRetry(5000)
    };
});

builder.Services.AddSingleton<ICacheService, RedisCacheService>();


var app = builder.Build();

// Настройка pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<PraceksAPI.MiddleWare.ApiKeyAuthMiddleware>();

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Health check с проверкой зависимостей
app.MapGet("/health", async () =>
{
    var health = new { status = "Healthy", timestamp = DateTime.UtcNow };
    // Можно добавить проверку Redis и PostgreSQL
    return Results.Ok(health);
});

app.Run();