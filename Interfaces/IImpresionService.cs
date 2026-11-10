using berllanoWorker.Models;

namespace berllanoWorker.Interfaces;

public interface IImpresionService
{
    Task<List<string>> ObtenerImpresorasAsync();
    Task ImprimirTicketAsync(TicketRequestDto request);
    Task ImprimirDocumentoAsync(DocumentoRequestDto request);
}
