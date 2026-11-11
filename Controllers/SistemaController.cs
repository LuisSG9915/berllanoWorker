using berllanoWorker.Interfaces;
using berllanoWorker.Models;
using berllanoWorker.Services;
using Microsoft.AspNetCore.Mvc;

namespace berllanoWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SistemaController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IConfiguracionCajaService _configCaja;

    public SistemaController(IConfiguration configuration, IConfiguracionCajaService configCaja)
    {
        _configuration = configuration;
        _configCaja = configCaja;
    }

    // Health check + auto-descubrimiento: el front lo consulta en
    // localhost:5000 para saber si esta PC tiene worker instalado, y en
    // cada worker de la tabla para mostrar "En linea" en el modal.
    [HttpGet("identidad")]
    public IActionResult Identidad()
    {
        var dto = new IdentidadDto
        {
            Hostname = NetworkInfoHelper.ObtenerHostname(),
            HostMdns = NetworkInfoHelper.ObtenerHostMdns(),
            IpsLan = NetworkInfoHelper.ObtenerIpsLan(),
            Puerto = _configuration.GetValue("Worker:Puerto", 5000),
            Version = NetworkInfoHelper.ObtenerVersion(),
            Status = "ONLINE"
        };
        return Ok(dto);
    }

    // GET /api/sistema/cajas?sucursal=N -> consolidado por caja: impresoras
    // de cat_RecursosWorker (con flag instaladaLocal en esta PC) + terminal
    // de t_banorte_usuarios (sin pass_banorte). Diagnostico para el Front.
    [HttpGet("cajas")]
    public async Task<IActionResult> Cajas([FromQuery] int? sucursal, CancellationToken ct)
    {
        return Ok(await _configCaja.ObtenerResumenCajasAsync(sucursal, ct));
    }
}
