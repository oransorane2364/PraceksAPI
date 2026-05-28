using PraceksAPI.Services;
using StackExchange.Redis;
using Polly;

var builder = WebApplication.CreateBuilder(args);




builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();



builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Регистрация фонового сервиса архивации
builder.Services.AddHostedService<ArchiveCleanupService>();

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = "localhost:6379";
    options.InstanceName = "PraceksAPI_";

    // Настройка таймаутов
    options.ConfigurationOptions = new ConfigurationOptions
    {
        EndPoints = { "localhost:6379" },
        ConnectTimeout = 5000,      // 5 секунд на подключение
        SyncTimeout = 5000,         // 5 секунд на операции
        AbortOnConnectFail = false, // ВАЖНО! Не падать при ошибке подключения
        ReconnectRetryPolicy = new LinearRetry(5000) // Попытки переподключения
    };
});

builder.Services.AddSingleton<ICacheService, RedisCacheService>();



var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
