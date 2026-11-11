using berllanoWorker.Models;

namespace berllanoWorker.Interfaces;

public interface ITerminalService
{
    Task<TerminalTransaccionResponseDto> InicializarLlavesAsync(TerminalLlavesRequestDto request, CancellationToken ct = default);
    Task<TerminalTransaccionResponseDto> CobrarAsync(TerminalCobroRequestDto request, CancellationToken ct = default);
    Task<TerminalTransaccionResponseDto> RetirarEfectivoAsync(TerminalRetiroRequestDto request, CancellationToken ct = default);
    Task<TerminalTransaccionResponseDto> CancelarTransaccionAsync(TerminalCancelacionRequestDto request, CancellationToken ct = default);
    Task<TerminalTransaccionResponseDto> VerificarTransaccionAsync(TerminalVerificarRequestDto request, CancellationToken ct = default);
    Task<List<TerminalCajaDto>> ObtenerCajasAsync(int? sucursal, CancellationToken ct = default);
}
