using berllanoWorker.Models;

namespace berllanoWorker.Interfaces;

public interface IBiometriaService
{
    Task<int> VerificarAsync(string empleado, CancellationToken ct = default);
    Task<BiometriaIdentificarResponseDto> IdentificarAsync(CancellationToken ct = default);
    Task<string> EnrolarHuellaAsync(string empleado, CancellationToken ct = default);
    Task RecargarCacheHuellasAsync(CancellationToken ct = default);
}
