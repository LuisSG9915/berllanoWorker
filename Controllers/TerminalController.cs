using berllanoWorker.Interfaces;
using berllanoWorker.Models;
using Microsoft.AspNetCore.Mvc;

namespace berllanoWorker.Controllers;

[ApiController]
[Route("api/terminal")]
public class TerminalController : ControllerBase
{
    private readonly ITerminalService _terminalService;

    public TerminalController(ITerminalService terminalService)
    {
        _terminalService = terminalService;
    }

    // La PWA puede mandar la caja por header global (useConsumoWorker.ts);
    // el body JSON tiene prioridad si trae valores explicitos.
    private void AplicarHeadersSucursalCaja(TerminalConfigOverrideDto request)
    {
        if (request.Sucursal is null
            && int.TryParse(Request.Headers["X-Sucursal"].FirstOrDefault(), out var suc))
            request.Sucursal = suc;
        if (request.Caja is null
            && int.TryParse(Request.Headers["X-Caja"].FirstOrDefault(), out var caja))
            request.Caja = caja;
    }

    [HttpPost("llaves")]
    public async Task<IActionResult> InicializarLlaves([FromBody] TerminalLlavesRequestDto? request, CancellationToken ct)
    {
        request ??= new();
        AplicarHeadersSucursalCaja(request);
        var result = await _terminalService.InicializarLlavesAsync(request, ct);
        if (!result.Aprobada)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, result);
        return Ok(result);
    }

    [HttpPost("cobro")]
    public async Task<IActionResult> Cobrar([FromBody] TerminalCobroRequestDto request, CancellationToken ct)
    {
        AplicarHeadersSucursalCaja(request);
        var result = await _terminalService.CobrarAsync(request, ct);
        if (!result.Aprobada)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("retiro")]
    public async Task<IActionResult> Retiro([FromBody] TerminalRetiroRequestDto request, CancellationToken ct)
    {
        AplicarHeadersSucursalCaja(request);
        var result = await _terminalService.RetirarEfectivoAsync(request, ct);
        if (!result.Aprobada)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("cancelacion")]
    public async Task<IActionResult> Cancelacion([FromBody] TerminalCancelacionRequestDto request, CancellationToken ct)
    {
        AplicarHeadersSucursalCaja(request);
        var result = await _terminalService.CancelarTransaccionAsync(request, ct);
        if (!result.Aprobada)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("verificar")]
    public async Task<IActionResult> Verificar([FromBody] TerminalVerificarRequestDto request, CancellationToken ct)
    {
        AplicarHeadersSucursalCaja(request);
        var result = await _terminalService.VerificarTransaccionAsync(request, ct);
        if (!result.Aprobada)
            return BadRequest(result);
        return Ok(result);
    }

    // Lista las cajas configuradas en t_banorte_usuarios (sin pass_banorte)
    // para poblar el selector de caja del Front o diagnosticar asignaciones.
    [HttpGet("cajas")]
    public async Task<IActionResult> Cajas([FromQuery] int? sucursal, CancellationToken ct)
    {
        var result = await _terminalService.ObtenerCajasAsync(sucursal, ct);
        return Ok(result);
    }
}
