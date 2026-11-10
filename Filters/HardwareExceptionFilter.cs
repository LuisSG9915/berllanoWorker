using berllanoWorker.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace berllanoWorker.Filters;

// Convierte las HardwareException en respuestas HTTP con el JSON estandar
// del worker: { status, error, mensaje }. El frontend mapea 'status' a SweetAlert.
public class HardwareExceptionFilter : IExceptionFilter
{
    private readonly ILogger<HardwareExceptionFilter> _logger;

    public HardwareExceptionFilter(ILogger<HardwareExceptionFilter> logger)
    {
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is HardwareException hex)
        {
            _logger.LogWarning("Hardware error {Status}: {Message}", hex.StatusCode, hex.Message);
            context.Result = new ObjectResult(new
            {
                status = hex.StatusCode,
                error = hex.GetType().Name,
                mensaje = hex.Message
            })
            {
                StatusCode = hex.StatusCode
            };
            context.ExceptionHandled = true;
        }
    }
}
