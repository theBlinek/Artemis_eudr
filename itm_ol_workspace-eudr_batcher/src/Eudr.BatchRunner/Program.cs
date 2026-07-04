using Eudr.BatchRunner;
using Eudr.BatchRunner.Handlers;
using Eudr.BatchRunner.Interfaces;
using Eudr.BatchRunner.Reconciliation;
using Eudr.BatchRunner.Repository;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<EudrOptions>(builder.Configuration.GetSection(EudrOptions.Section));

builder.Services.AddSingleton<IConnectionFactory>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>().GetSection("DatabaseSettings");
    var cs = $"DataSource={cfg["DB_HOST"]};Port={cfg["DB_PORT"] ?? "3050"};" +
             $"Database={cfg["DB_PATH"]};User={cfg["DB_USER"] ?? "SYSDBA"};" +
             $"Password={cfg["DB_PASSWORD"]};Charset=NONE";
    return new FirebirdConnectionFactory(cs);
});

builder.Services.AddSingleton<IEudrRepository, FirebirdEudrRepository>();
builder.Services.AddSingleton<FifoEngine>();

builder.Services.AddSingleton<IHandler, RinHandler>();
builder.Services.AddSingleton<IHandler, RprHandler>();
builder.Services.AddSingleton<IHandler, ZwrHandler>();
builder.Services.AddSingleton<IHandler, PksHandler>();
builder.Services.AddSingleton<IHandler, RzuHandler>();
builder.Services.AddSingleton<IHandler, OutHandler>();
builder.Services.AddSingleton<IHandler, RinReverseHandler>();
builder.Services.AddSingleton<IHandler, RprReverseHandler>();
builder.Services.AddSingleton<IHandler, ZwrReverseHandler>();
builder.Services.AddSingleton<IHandler, RzuReverseHandler>();
builder.Services.AddSingleton<IHandler, OutReverseHandler>();

builder.Services.AddSingleton<Dispatcher>();
builder.Services.AddSingleton<ReconciliationService>();
builder.Services.AddSingleton<BatchRunnerService>();

var app = builder.Build();

app.MapPost("/api/batch/run", (BatchRunnerService batch, ILogger<Program> logger) =>
{
    if (!batch.TryStartPending())
    {
        logger.LogWarning("Batch trigger received but a run is already in progress");
        return Results.Conflict(new { message = "Batch already running" });
    }

    logger.LogInformation("Batch trigger received — run started");
    return Results.Accepted("/api/batch/run", new { message = "Batch started" });
});

app.MapGet("/api/batch/status", (BatchRunnerService batch) =>
    Results.Ok(new { running = batch.IsRunning }));

app.Run();
