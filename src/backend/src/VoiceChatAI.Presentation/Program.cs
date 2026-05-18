using Microsoft.EntityFrameworkCore;
using Prometheus;
using VoiceChatAI.Application.Services;
using VoiceChatAI.Infrastructure.Data;
using VoiceChatAI.Infrastructure.Messaging;
using VoiceChatAI.Infrastructure.Repositories;
using VoiceChatAI.Infrastructure.Services;
using VoiceChatAI.Domain.Interfaces;
using VoiceChatAI.Presentation.Hubs;
using VoiceChatAI.Presentation.Middleware;
using VoiceChatAI.Presentation.Services;

var builder = WebApplication.CreateBuilder(args);

// ─── Configuration ───────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

// ─── Database ────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Host=postgres;Port=5432;Database=voicechatai;Username=voicechatai;Password=voicechatai_secret";
    options.UseNpgsql(connectionString, npgsql =>
    {
        npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
        npgsql.EnableRetryOnFailure(3);
    });
});

// ─── Repositories ────────────────────────────────────────────────
builder.Services.AddScoped<IRoomRepository, RoomRepository>();
builder.Services.AddScoped<ITranscriptRepository, TranscriptRepository>();
builder.Services.AddScoped<IMeetingRecordingRepository, MeetingRecordingRepository>();

// ─── Application Services ────────────────────────────────────────
builder.Services.AddScoped<IRoomService, RoomService>();

// ─── RabbitMQ ────────────────────────────────────────────────────
builder.Services.Configure<RabbitMqOptions>(
    builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton<RabbitMqConnectionFactory>();
builder.Services.AddHostedService<AudioIngestionConsumer>();
builder.Services.AddHostedService<TranscriptConsumer>();
builder.Services.AddHostedService<AiOrchestratorService>();

// ─── Recording Service ───────────────────────────────────────────
builder.Services.Configure<RecordingOptions>(
    builder.Configuration.GetSection(RecordingOptions.SectionName));
builder.Services.AddSingleton<MeetingRecordingService>();

// ─── Ollama ──────────────────────────────────────────────────────
builder.Services.Configure<OllamaOptions>(
    builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.AddHttpClient<OllamaService>(client =>
{
    var baseUrl = builder.Configuration.GetValue<string>("Ollama:BaseUrl") ?? "http://ollama:11434";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(60);
});

// ─── Whisper ─────────────────────────────────────────────────────
builder.Services.Configure<WhisperOptions>(
    builder.Configuration.GetSection(WhisperOptions.SectionName));
builder.Services.AddHttpClient<WhisperService>(client =>
{
    var baseUrl = builder.Configuration.GetValue<string>("Whisper:BaseUrl") ?? "http://whisper-livekit:8080";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(300);
});

// Named client for WhisperLiveKit summarization API
builder.Services.AddHttpClient("WhisperLiveKit", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// ─── SignalR ─────────────────────────────────────────────────────
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// ─── CORS ────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });

    options.AddPolicy("SignalR", policy =>
    {
        policy.WithOrigins("http://localhost:4200", "http://localhost:5000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// ─── Health Checks ───────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>();

// ─── Controllers ─────────────────────────────────────────────────
builder.Services.AddControllers();

// ─── Swagger ─────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ─── App Pipeline ────────────────────────────────────────────────
var app = builder.Build();

// Ensure database is created on startup
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var db = sp.GetRequiredService<AppDbContext>();
    try
    {
        db.Database.EnsureCreated();
        app.Logger.LogInformation("Database ensured created successfully");

        // Apply missing migrations for columns added after initial schema creation
        // (EnsureCreated doesn't update existing tables)
        ApplyMissingMigrations(db, sp);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not ensure database created. Database may not be ready yet.");
    }
}

static void ApplyMissingMigrations(AppDbContext db, IServiceProvider sp)
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    try
    {
        // Add columns to Transcripts that were added after initial schema creation
        // (EnsureCreated doesn't update existing tables)
        var migrations = new[]
        {
            "ALTER TABLE \"Transcripts\" ADD COLUMN IF NOT EXISTS \"Metadata\" jsonb NULL",
            "ALTER TABLE \"Transcripts\" ADD COLUMN IF NOT EXISTS \"SpeakerId\" varchar(50) NULL DEFAULT 'unknown'",
            "ALTER TABLE \"Transcripts\" ADD COLUMN IF NOT EXISTS \"Embedding\" text NULL",
        };

        foreach (var sql in migrations)
        {
            try
            {
                db.Database.ExecuteSqlRaw(sql);
                logger.LogInformation("Applied migration: {Sql}", sql);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not apply migration: {Sql}", sql);
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not apply missing migrations");
    }
}

// Middleware
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Swagger
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// CORS
app.UseCors("SignalR");

// Prometheus metrics
app.UseMetricServer();
app.UseHttpMetrics();

// Health checks
app.MapHealthChecks("/health");

// SignalR Hub
app.MapHub<MeetingHub>("/hubs/meeting");

// API Controllers
app.MapControllers();

// API endpoints
app.MapGet("/", () => Results.Ok(new { service = "VoiceChatAI", status = "running" }));

app.Run();

// Make Program class accessible for integration tests
public partial class Program { }
