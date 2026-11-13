using berllanoWorker.Models;

namespace berllanoWorker.Interfaces;

// Resuelve "que recurso fisico corresponde a esta (sucursal, caja)" contra
// cberp, con cache en memoria + archivo para sobrevivir cortes de WAN.
public interface IConfiguracionCajaService
{
    // Alias genericos ("Tickets","Documentos","Default", vacio) -> lookup en
    // cat_RecursosWorker; nombre fisico instalado en Windows -> passthrough;
    // nombre custom -> lookup literal. Sin fila -> 422.
    Task<string> ResolverImpresoraCajaAsync(int? sucursal, int? caja,
        string? recursoSolicitado, bool forzarRecarga = false, CancellationToken ct = default);

    // t_banorte_usuarios por (cia, sucursal, caja) + overrides del request;
    // credenciales locales de appsettings solo aplican a la caja default.
    Task<TerminalCredencialesDto> ResolverTerminalCajaAsync(
        TerminalConfigOverrideDto request, CancellationToken ct = default);

    // Filas de t_banorte_usuarios (sin pass_banorte).
    Task<List<TerminalCajaDto>> ObtenerCajasTerminalAsync(int? sucursal, CancellationToken ct = default);

    // Lista de terminales con información de conexión (para GET /api/terminal/terminales)
    Task<List<TerminalInfoDto>> ObtenerTerminalesConEstadoAsync(int? sucursal, bool verificarPing, CancellationToken ct = default);

    // Resumen consolidado: impresoras de cat_RecursosWorker (con flag
    // instaladaLocal) + terminal de t_banorte_usuarios por cada caja.
    Task<List<CajaResumenDto>> ObtenerResumenCajasAsync(int? sucursal, CancellationToken ct = default);

    // Obtener connection string para consultas directas
    string ObtenerConnectionString();
}
