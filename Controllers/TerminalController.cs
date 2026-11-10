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

    [HttpPost("llaves")]
    public async Task<IActionResult> InicializarLlaves([FromBody] TerminalLlavesRequestDto? request, CancellationToken ct)
    {
        var result = await _terminalService.InicializarLlavesAsync(request ?? new(), ct);
        if (!result.Aprobada)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, result);
        return Ok(result);
    }

    [HttpPost("cobro")]
    public async Task<IActionResult> Cobrar([FromBody] TerminalCobroRequestDto request, CancellationToken ct)
    {
        var result = await _terminalService.CobrarAsync(request, ct);
        if (!result.Aprobada)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("retiro")]
    public async Task<IActionResult> Retiro([FromBody] TerminalRetiroRequestDto request, CancellationToken ct)
    {
        var result = await _terminalService.RetirarEfectivoAsync(request, ct);
        if (!result.Aprobada)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("cancelacion")]
    public async Task<IActionResult> Cancelacion([FromBody] TerminalCancelacionRequestDto request, CancellationToken ct)
    {
        var result = await _terminalService.CancelarTransaccionAsync(request, ct);
        if (!result.Aprobada)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("verificar")]
    public async Task<IActionResult> Verificar([FromBody] TerminalVerificarRequestDto request, CancellationToken ct)
    {
        var result = await _terminalService.VerificarTransaccionAsync(request, ct);
        if (!result.Aprobada)
            return BadRequest(result);
        return Ok(result);
    }
}
