using System.Text;
using berllanoWorker.Filters;
using berllanoWorker.Interfaces;
using berllanoWorker.Middleware;
using berllanoWorker.Services;
using Serilog;

// Codepages heredadas (437/850/858...) para los comandos ESC/POS.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

var puerto = builder.Configuration.GetValue("Worker:Puerto", 5000);
builder.WebHost.UseUrls($"http://0.0.0.0:{puerto}");
builder.Host.UseWindowsService();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "worker-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

builder.Services.AddSingleton<IImpresionService, ImpresionService>();

builder.Services.AddControllers(options =>
{
    options.Filters.Add<HardwareExceptionFilter>();
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Private Network Access: los navegadores (especialmente tablets/moviles en
// la LAN) exigen esta cabecera en el preflight cuando la PWA, servida desde
// un origen publico, llama a un recurso en la red local.
app.Use(async (context, next) =>
{
    if (context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
        context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";

    if (context.Request.Method == "OPTIONS")
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
        context.Response.Headers["Access-Control-Allow-Headers"] = "*";
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }

    await next();
});

app.UseCors("AllowAll");
app.UseMiddleware<WorkerKeyMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();
app.Run();
