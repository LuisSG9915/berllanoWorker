using berllanoWorker.Models;
using berllanoWorker.Services;
using Microsoft.AspNetCore.Mvc;

namespace berllanoWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SistemaController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public SistemaController(IConfiguration configuration)
    {
        _configuration = configuration;
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
}
