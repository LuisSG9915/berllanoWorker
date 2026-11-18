using berllanoWorker.Interfaces;
using berllanoWorker.Models;
using Microsoft.AspNetCore.Mvc;

namespace berllanoWorker.Controllers;

[ApiController]
[Route("api/biometria")]
public class BiometriaController : ControllerBase
{
    private readonly IBiometriaService _biometriaService;

    public BiometriaController(IBiometriaService biometriaService)
    {
        _biometriaService = biometriaService;
    }

    // 1:1 -> 1 si la huella coincide con la plantilla del empleado, 0 si no.
    [HttpPost("verificar/{empleado}")]
    public async Task<IActionResult> Verificar([FromRoute] string empleado, CancellationToken ct)
    {
        int resultado = await _biometriaService.VerificarAsync(empleado, ct);
        return Ok(resultado);
    }

    // 1:N -> { "claveEmpleado": "0003" } o 404 si nadie coincide.
    [HttpPost("identificar")]
    public async Task<IActionResult> Identificar(CancellationToken ct)
    {
        var resultado = await _biometriaService.IdentificarAsync(ct);
        return Ok(resultado);
    }

    // Enrola la huella del empleado -> string Base64 del DPFP.Template.
    [HttpPost("huella")]
    public async Task<IActionResult> Enrolar([FromBody] BiometriaHuellaRequestDto request, CancellationToken ct)
    {
        string templateBase64 = await _biometriaService.EnrolarHuellaAsync(request.Empleado, ct);
        return Ok(templateBase64);
    }

    // Fuerza la recarga del padron de huellas desde cberp.
    [HttpPost("recargar-cache")]
    public async Task<IActionResult> RecargarCache(CancellationToken ct)
    {
        await _biometriaService.RecargarCacheHuellasAsync(ct);
        return Ok(new { status = 200, mensaje = "Padron de huellas recargado" });
    }
}
