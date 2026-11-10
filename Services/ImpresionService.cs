using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Printing;
using berllanoWorker.Exceptions;
using berllanoWorker.Interfaces;
using berllanoWorker.Models;

namespace berllanoWorker.Services;

// Singleton: un solo objeto maneja el acceso a las impresoras fisicas.
// Un semaphore por nombre de impresora evita que dos tickets se crucen.
public class ImpresionService : IImpresionService
{
    private readonly ILogger<ImpresionService> _logger;
    private readonly int _codePage;
    private readonly int _columnas;
    private readonly string _sumatraPath;
    private readonly int _timeoutDescargaSeg;
    private readonly long _tamMaximoBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly HttpClient _httpClient = new();

    public ImpresionService(IConfiguration configuration, ILogger<ImpresionService> logger)
    {
        _logger = logger;
        _codePage = configuration.GetValue("Impresion:CodePage", 858);
        _columnas = configuration.GetValue("Impresion:Columnas", 40);
        _sumatraPath = configuration.GetValue<string>("Impresion:SumatraPdfPath") ?? "tools\\SumatraPDF.exe";
        _timeoutDescargaSeg = configuration.GetValue("Impresion:TimeoutDescargaSegundos", 15);
        _tamMaximoBytes = configuration.GetValue("Impresion:TamMaximoDocumentoMB", 50) * 1024L * 1024L;
        _httpClient.Timeout = TimeSpan.FromSeconds(_timeoutDescargaSeg);
    }

    public Task<List<string>> ObtenerImpresorasAsync()
    {
        var impresoras = new List<string>();
        foreach (string nombre in PrinterSettings.InstalledPrinters)
            impresoras.Add(nombre);
        return Task.FromResult(impresoras);
    }

    public async Task ImprimirTicketAsync(TicketRequestDto request)
    {
        ValidarImpresora(request.Recurso);

        var bytes = EscPosBuilder.Build(request.TicketDataset, _codePage, _columnas);

        await EjecutarConBloqueo(request.Recurso, () =>
        {
            if (!RawPrinterHelper.SendBytesToPrinter(request.Recurso, bytes))
                throw new HardwareDisconnectedException(
                    $"La impresora '{request.Recurso}' no respondio al enviar el ticket. Verifica que este encendida y conectada.");

            _logger.LogInformation("Ticket enviado a '{Recurso}' ({Bytes} bytes)", request.Recurso, bytes.Length);
            return Task.CompletedTask;
        });
    }

    public async Task ImprimirDocumentoAsync(DocumentoRequestDto request)
    {
        ValidarImpresora(request.Recurso);

        if (!Uri.TryCreate(request.UrlFile, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new HardwareValidationException($"url_file invalida: '{request.UrlFile}'. Solo http/https.");

        var archivoTemp = Path.Combine(Path.GetTempPath(), $"worker_{Guid.NewGuid():N}.pdf");
        try
        {
            await DescargarArchivoAsync(uri, archivoTemp);

            await EjecutarConBloqueo(request.Recurso, () =>
            {
                ImprimirPdfConSumatra(request.Recurso, archivoTemp);
                _logger.LogInformation("Documento {Url} impreso en '{Recurso}'", request.UrlFile, request.Recurso);
                return Task.CompletedTask;
            });
        }
        finally
        {
            if (File.Exists(archivoTemp))
                File.Delete(archivoTemp);
        }
    }

    private async Task DescargarArchivoAsync(Uri uri, string destino)
    {
        try
        {
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                throw new HardwareValidationException($"La URL respondio {(int)response.StatusCode}. No se pudo descargar el documento.");

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var file = File.Create(destino);

            var buffer = new byte[81920];
            long total = 0;
            int leido;
            while ((leido = await stream.ReadAsync(buffer)) > 0)
            {
                total += leido;
                if (total > _tamMaximoBytes)
                    throw new HardwareValidationException($"El documento excede el maximo de {_tamMaximoBytes / 1024 / 1024} MB.");
                await file.WriteAsync(buffer.AsMemory(0, leido));
            }

            if (total == 0)
                throw new HardwareValidationException("La URL devolvio un archivo vacio.");
        }
        catch (HardwareValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new HardwareValidationException($"No se pudo descargar el documento: {ex.Message}");
        }

        await ValidarEsPdfAsync(destino);
    }

    // Revisa la firma %PDF para que un HTML o imagen equivocada regrese 422
    // en lugar de fallar dentro de SumatraPDF como un 503 generico.
    private static async Task ValidarEsPdfAsync(string archivo)
    {
        var firma = new byte[5];
        await using (var fs = new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var leidos = await fs.ReadAsync(firma);
            if (leidos < 5)
                throw new HardwareValidationException("El archivo descargado no es un PDF valido (muy pequeno).");
        }

        if (firma[0] != 0x25 || firma[1] != 0x50 || firma[2] != 0x44 || firma[3] != 0x46 || firma[4] != 0x2D) // %PDF-
            throw new HardwareValidationException("El archivo descargado no es un PDF valido (falta la firma %PDF).");
    }

    private void ImprimirPdfConSumatra(string impresora, string archivo)
    {
        var sumatra = Path.IsPathRooted(_sumatraPath)
            ? _sumatraPath
            : Path.Combine(AppContext.BaseDirectory, _sumatraPath);

        if (!File.Exists(sumatra))
            throw new HardwareDisconnectedException(
                $"No se encontro SumatraPDF en '{sumatra}'. Copia SumatraPDF.exe a la carpeta tools del worker.");

        var psi = new ProcessStartInfo
        {
            FileName = sumatra,
            Arguments = $"-print-to \"{impresora}\" -print-settings \"fit,letter\" -silent \"{archivo}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            throw new HardwareDisconnectedException("No se pudo iniciar SumatraPDF para imprimir el documento.");

        if (!proc.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            try { proc.Kill(); } catch { /* no-op */ }
            throw new HardwareBusyException("La impresion del documento tardo mas de 60 segundos.");
        }

        if (proc.ExitCode != 0)
            throw new HardwareDisconnectedException(
                $"SumatraPDF fallo al imprimir en '{impresora}' (codigo {proc.ExitCode}).");
    }

    private void ValidarImpresora(string recurso)
    {
        if (string.IsNullOrWhiteSpace(recurso))
            throw new HardwareValidationException("El campo 'recurso' (nombre de impresora) es obligatorio.");

        var existe = PrinterSettings.InstalledPrinters
            .Cast<string>()
            .Any(p => string.Equals(p, recurso, StringComparison.OrdinalIgnoreCase));

        if (!existe)
            throw new HardwareValidationException(
                $"La impresora '{recurso}' no existe en este equipo. Revisa el nombre en Windows o en cat_RecursosWorker.");
    }

    private async Task EjecutarConBloqueo(string recurso, Func<Task> accion)
    {
        var semaforo = _locks.GetOrAdd(recurso, _ => new SemaphoreSlim(1, 1));

        // Spec: rechazo inmediato con 423 si la impresora ya esta ocupada.
        if (!await semaforo.WaitAsync(0))
            throw new HardwareBusyException(
                $"La impresora '{recurso}' esta imprimiendo otro documento. Espera un momento.");

        try
        {
            await accion();
        }
        finally
        {
            semaforo.Release();
        }
    }
}
