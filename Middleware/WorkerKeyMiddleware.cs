namespace berllanoWorker.Middleware;

// API key compartida por sucursal: exige header X-Worker-Key en todos los
// endpoints de hardware (/api/* menos /api/sistema, que es el health check
// de descubrimiento y no afecta hardware). Evita que cualquier dispositivo
// en el Wi-Fi de la sucursal mande trabajos de impresion.
public class WorkerKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _workerKey;

    public WorkerKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _workerKey = configuration.GetValue<string>("Worker:WorkerKey");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        var esHardware = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/sistema", StringComparison.OrdinalIgnoreCase);

        if (esHardware
            && !context.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_workerKey))
        {
            var header = context.Request.Headers["X-Worker-Key"].FirstOrDefault();
            if (!string.Equals(header, _workerKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    status = 401,
                    error = "Unauthorized",
                    mensaje = "X-Worker-Key invalida o ausente"
                });
                return;
            }
        }

        await _next(context);
    }
}
