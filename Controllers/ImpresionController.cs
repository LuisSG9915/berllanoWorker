using berllanoWorker.Exceptions;
using berllanoWorker.Interfaces;
using berllanoWorker.Models;
using berllanoWorker.Services;
using Microsoft.AspNetCore.Mvc;

namespace berllanoWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImpresionController : ControllerBase
{
    private readonly IImpresionService _impresion;

    public ImpresionController(IImpresionService impresion)
    {
        _impresion = impresion;
    }

    // La PWA puede mandar la caja por header global (useConsumoWorker.ts);
    // el body JSON tiene prioridad si trae valores explicitos.
    private (int? Suc, int? Caj) HeadersSucursalCaja()
    {
        int? suc = int.TryParse(Request.Headers["X-Sucursal"].FirstOrDefault(), out var s) ? s : null;
        int? caj = int.TryParse(Request.Headers["X-Caja"].FirstOrDefault(), out var c) ? c : null;
        return (suc, caj);
    }

    // GET /api/impresion/impresoras -> lista de impresoras instaladas en esta PC
    [HttpGet("impresoras")]
    public async Task<IActionResult> Impresoras()
    {
        return Ok(await _impresion.ObtenerImpresorasAsync());
    }

    // POST /api/impresion/ticket -> imprime ticket ESC/POS en la impresora 'recurso'
    [HttpPost("ticket")]
    public async Task<IActionResult> Ticket([FromBody] TicketRequestDto request)
    {
        if (request is null || request.TicketDataset is null || request.TicketDataset.Count == 0)
            throw new HardwareValidationException("El cuerpo de la peticion debe traer 'ticket_dataset'.");

        var (suc, caj) = HeadersSucursalCaja();
        request.Sucursal ??= suc;
        request.Caja ??= caj;

        await _impresion.ImprimirTicketAsync(request);
        return Ok(new { status = 200, mensaje = "Ticket enviado a cola de impresion" });
    }

    // POST /api/impresion/documento -> descarga un PDF/imagen y lo imprime en carta
    [HttpPost("documento")]
    public async Task<IActionResult> Documento([FromBody] DocumentoRequestDto request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.UrlFile))
            throw new HardwareValidationException("El cuerpo de la peticion debe traer 'url_file'.");

        var (suc, caj) = HeadersSucursalCaja();
        request.Sucursal ??= suc;
        request.Caja ??= caj;

        await _impresion.ImprimirDocumentoAsync(request);
        return Ok(new { status = 200, mensaje = "Documento enviado a cola de impresion" });
    }

    // GET /api/impresion/prueba?recurso=X[&sucursal=N&caja=M] -> diagnostico
    [HttpGet("prueba")]
    public async Task<IActionResult> Prueba([FromQuery] string? recurso,
        [FromQuery] int? sucursal, [FromQuery] int? caja)
    {
        var ticket = new TicketRequestDto
        {
            Recurso = recurso ?? string.Empty,
            Sucursal = sucursal,
            Caja = caja,
            TicketDataset =
            [
                new TicketLineaDto { Consecutivo = 1, Modo = "CENTRADO_GRANDE_NEGRITA", Data = "BERLLANO WORKER" },
                new TicketLineaDto { Consecutivo = 2, Modo = "CENTRADO_NORMAL", Data = NetworkInfoHelper.ObtenerHostname() },
                new TicketLineaDto { Consecutivo = 3, Modo = "TEXTO_PLANO", Data = "----------------------------------------" },
                new TicketLineaDto { Consecutivo = 4, Modo = "TEXTO_PLANO", Data = $"Prueba {DateTime.Now:dd/MM/yyyy HH:mm:ss}" },
                new TicketLineaDto { Consecutivo = 5, Modo = "TEXTO_PLANO", Data = "Acentos: áéíóú ñÑ ¿? ¡!" },
                new TicketLineaDto { Consecutivo = 6, Modo = "DERECHA_NEGRITA", Data = "TEST OK" },
                new TicketLineaDto { Consecutivo = 7, Modo = "CORTE_PAPEL", Data = "" }
            ]
        };

        await _impresion.ImprimirTicketAsync(ticket);
        return Ok(new { status = 200, mensaje = $"Ticket de prueba enviado a '{recurso}'" });
    }
}
