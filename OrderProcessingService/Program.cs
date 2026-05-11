using Microsoft.EntityFrameworkCore;
using OrderProcessingService.Data;
using OrderProcessingService.Messaging;
using OrderProcessingService.Processing;
using Prometheus;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, lc) => lc
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    // -- Configuration --
    builder.Services.Configure<RabbitMqOptions>(
        builder.Configuration.GetSection(RabbitMqOptions.SectionName));

    // -- EF Core / PostgreSQL --
    builder.Services.AddDbContext<AppDbContext>(opt =>
        opt.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
                      ?? throw new InvalidOperationException("Missing ConnectionStrings:Postgres")));

    // -- Messaging --
    builder.Services.AddSingleton<RabbitMqConnection>();
    builder.Services.AddSingleton<IOrderPublisher, OrderPublisher>();

    // -- Processing --
    builder.Services.AddScoped<OrderProcessor>();
    builder.Services.AddHostedService<OrderConsumer>();

    // -- Web --
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

    // Initialize DB + seed inventory.
    await DbInitializer.InitializeAsync(app.Services);

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseSerilogRequestLogging();

    // Prometheus middleware: track HTTP metrics and expose /metrics.
    app.UseHttpMetrics();

    app.MapControllers();
    app.MapMetrics();           // GET /metrics
    app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
