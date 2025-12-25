using DiagManTestApp.BackgroundServices;
using DiagManTestApp.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Register services with intentional bugs for DiagMan testing
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<OrderProcessingService>();

// External API service with HttpClient
builder.Services.AddHttpClient<ExternalApiService>();

// Background service for automatic memory leak (enable via config)
builder.Services.AddHostedService<MemoryLeakBackgroundService>();

// Configuration service - only register if not in "crash-on-startup" mode
var scenarioMode = builder.Configuration["Scenarios:Mode"] ?? "normal";
if (scenarioMode != "crash-on-startup")
{
    // Skip ConfigurationService registration to avoid startup crash
    // When testing the startup crash scenario, set Scenarios:Mode=crash-on-startup
}
else
{
    // This will crash on startup if PAYMENT_API_SECRET is not set
    builder.Services.AddSingleton<ConfigurationService>();
}

// Health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.MapControllers();

// Health check endpoint
app.MapHealthChecks("/health");

// Simple status endpoint
app.MapGet("/", () => new
{
    service = "DiagMan Test App",
    version = "1.0.0",
    description = "Application with intentional bugs for testing DiagMan diagnostics",
    scenarios = new[]
    {
        "POST /api/diagnostics/memory-leak - Trigger memory leak (OOM)",
        "POST /api/diagnostics/deadlock - Trigger deadlock scenario",
        "POST /api/diagnostics/timeout-cascade - Trigger cascading timeouts",
        "GET /api/diagnostics/stats - Get current stats"
    }
});

app.Run();
