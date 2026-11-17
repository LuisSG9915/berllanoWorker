using System.Text.Json;
using berllanoWorker.Exceptions;
using berllanoWorker.Interfaces;
using berllanoWorker.Models;
using Microsoft.Data.SqlClient;
using DPFP;
using DPFP.Capture;
using DPFP.Error;
using DPFP.Processing;
using DPFP.Verification;

namespace berllanoWorker.Services;

// Singleton: un solo lector USB fisico, por eso TODA captura pasa por
// _lectorLock (SemaphoreSlim(1,1)); la segunda peticion concurrente recibe 423.
// El padron de plantillas (cat_nomina_trabajadores.huella, varbinary(max) con
// DPFP.Template.Bytes crudo) se cachea en memoria por CacheHuellasMinutos con
// respaldo en ArchivoCacheLocal (JSON base64) para cortes de WAN.
public class BiometriaService : IBiometriaService
{
    private readonly ILogger<BiometriaService> _logger;
    private readonly string _connString;
    private readonly TimeSpan _timeoutCaptura;
    private readonly TimeSpan _cacheHuellas;
    private readonly string _archivoCacheLocal;
    private readonly bool _guardarEnrolamientoEnBd;

    private readonly SemaphoreSlim _lectorLock = new(1, 1);
    private readonly object _cacheSync = new();

    private sealed record EntradaHuella(byte[] Bytes, DateTime CargadoUtc);
    private sealed record CacheArchivo(DateTime ActualizadoUtc, Dictionary<string, string> Huellas);

    private Dictionary<string, EntradaHuella> _huellas = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _huellasCargadoUtc = DateTime.MinValue;

    public BiometriaService(IConfiguration configuration, ILogger<BiometriaService> logger)
    {
        _logger = logger;
        _connString = configuration.GetConnectionString("cberp") ?? string.Empty;
        _timeoutCaptura = TimeSpan.FromSeconds(
            configuration.GetValue("Biometria:TimeoutCapturaSegundos", 15));
        _cacheHuellas = TimeSpan.FromMinutes(
            configuration.GetValue("Biometria:CacheHuellasMinutos", 10));
        _archivoCacheLocal = configuration.GetValue<string>("Biometria:ArchivoCacheLocal")
            ?? @"C:\berllanoWorker\data\huellas_cache.json";
        _guardarEnrolamientoEnBd = configuration.GetValue("Biometria:GuardarEnrolamientoEnBd", false);
    }

    // -----------------------------------------------------------------
    // 1:1 — verificar la huella de UN empleado contra su plantilla
    // -----------------------------------------------------------------
    public async Task<int> VerificarAsync(string empleado, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(empleado))
            throw new HardwareValidationException("El parametro 'empleado' es obligatorio.");

        // Sin plantilla registrada no se enciende el lector: 404 inmediato.
        var templateBytes = await ObtenerHuellaEmpleadoAsync(empleado.Trim(), ct);
        if (templateBytes is null || templateBytes.Length == 0)
            throw new HardwareNotFoundException($"El empleado '{empleado}' no tiene huella registrada.");

        if (!await _lectorLock.WaitAsync(0, ct))
            throw new HardwareBusyException("El lector de huellas esta ocupado por otra operacion.");

        try
        {
            var sample = await CapturarMuestraAsync(_timeoutCaptura, ct);
            var features = ExtraerCaracteristicas(sample, DataPurpose.Verification);
            if (features is null) return 0;

            var template = CrearTemplate(templateBytes);
            if (template is null)
                throw new HardwareNotFoundException(
                    $"La plantilla de huella del empleado '{empleado}' esta corrupta.");

            var result = new Verification.Result();
            try
            {
                var verificador = new Verification();
                verificador.Verify(features, template, ref result);
            }
            catch (Exception ex) when (EsFalloMotorSdk(ex))
            {
                throw ErrorMotorSdk("verificacion", ex);
            }
            return result.Verified ? 1 : 0;
        }
        finally
        {
            _lectorLock.Release();
        }
    }

    // -----------------------------------------------------------------
    // 1:N — identificar quien puso el dedo contra todo el padron activo
    // -----------------------------------------------------------------
    public async Task<BiometriaIdentificarResponseDto> IdentificarAsync(CancellationToken ct = default)
    {
        await AsegurarCacheHuellasAsync(ct);

        if (!await _lectorLock.WaitAsync(0, ct))
            throw new HardwareBusyException("El lector de huellas esta ocupado por otra operacion.");

        try
        {
            var sample = await CapturarMuestraAsync(_timeoutCaptura, ct);
            var features = ExtraerCaracteristicas(sample, DataPurpose.Verification);
            if (features is null)
                throw new HardwareNotFoundException("Muestra de huella de mala calidad, intente de nuevo.");

            KeyValuePair<string, EntradaHuella>[] padron;
            lock (_cacheSync) padron = _huellas.ToArray();

            try
            {
                var verificador = new Verification();
                foreach (var (clave, entrada) in padron)
                {
                    var template = CrearTemplate(entrada.Bytes);
                    if (template is null) continue; // plantilla corrupta: se omite

                    var result = new Verification.Result();
                    verificador.Verify(features, template, ref result);
                    if (result.Verified)
                        return new BiometriaIdentificarResponseDto { ClaveEmpleado = clave };
                }
            }
            catch (Exception ex) when (EsFalloMotorSdk(ex))
            {
                throw ErrorMotorSdk("verificacion 1:N", ex);
            }

            throw new HardwareNotFoundException("Huella no identificada en el padron de trabajadores.");
        }
        finally
        {
            _lectorLock.Release();
        }
    }

    // -----------------------------------------------------------------
    // Enrolamiento — captura N muestras y consolida la plantilla
    // -----------------------------------------------------------------
    public async Task<string> EnrolarHuellaAsync(string empleado, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(empleado))
            throw new HardwareValidationException("El parametro 'empleado' es obligatorio.");
        var emp = empleado.Trim();

        if (!await _lectorLock.WaitAsync(0, ct))
            throw new HardwareBusyException("El lector de huellas esta ocupado por otra operacion.");

        try
        {
            Enrollment enroller;
            try { enroller = new Enrollment(); }
            catch (Exception ex) when (EsFalloMotorSdk(ex)) { throw ErrorMotorSdk("enrolamiento", ex); }

            const int maxIntentos = 8; // candado contra lecturas borrosas en bucle
            var intentos = 0;

            while (enroller.TemplateStatus != Enrollment.Status.Ready
                && enroller.TemplateStatus != Enrollment.Status.Failed
                && intentos < maxIntentos)
            {
                intentos++;
                var sample = await CapturarMuestraAsync(_timeoutCaptura, ct);
                var features = ExtraerCaracteristicas(sample, DataPurpose.Enrollment);
                if (features is null) continue;

                try { enroller.AddFeatures(features); }
                catch (SDKException ex)
                {
                    _logger.LogWarning("Enrollment rechazo features (intento {N}): {Msg}", intentos, ex.Message);
                }
                catch (Exception ex) when (EsFalloMotorSdk(ex)) { throw ErrorMotorSdk("enrolamiento", ex); }
            }

            if (enroller.TemplateStatus == Enrollment.Status.Failed)
                throw new HardwareValidationException("El lector no pudo procesar la huella, intenta de nuevo.");

            if (enroller.TemplateStatus != Enrollment.Status.Ready)
                throw new HardwareValidationException(
                    "No se pudo consolidar una plantilla estable, intenta de nuevo.");

            var template = enroller.Template;
            if (template is null)
                throw new HardwareValidationException("El enrolamiento no produjo plantilla, intenta de nuevo.");

            var templateBytes = template.Bytes;
            var templateBase64 = Convert.ToBase64String(templateBytes);

            if (_guardarEnrolamientoEnBd)
            {
                await GuardarHuellaBdAsync(emp, templateBytes, ct);
                ActualizarCacheLocal(emp, templateBytes);
            }

            return templateBase64;
        }
        finally
        {
            _lectorLock.Release();
        }
    }

    // Fuerza recarga del padron desde cberp (con respaldo en archivo local).
    public async Task RecargarCacheHuellasAsync(CancellationToken ct = default)
    {
        try
        {
            var padron = await ConsultarHuellasBdAsync(ct);
            if (padron.Count > 0)
            {
                lock (_cacheSync)
                {
                    _huellas = padron;
                    _huellasCargadoUtc = DateTime.UtcNow;
                }
                _ = PersistirCacheArchivoAsync();
                _logger.LogInformation("Cache de huellas recargado desde cberp: {N} plantillas.", padron.Count);
                return;
            }
            _logger.LogWarning("cberp respondio 0 plantillas de huella; se conserva cache previo.");
        }
        catch (Exception ex) when (ex is SqlException or TimeoutException or HardwareException)
        {
            _logger.LogWarning("cberp inalcanzable para padron de huellas: {Msg}. Usando cache local.", ex.Message);
        }

        var desdeArchivo = await LeerCacheArchivoAsync();
        if (desdeArchivo.Count > 0)
        {
            lock (_cacheSync)
            {
                _huellas = desdeArchivo;
                // Se marca como recien cargado para no golpear la WAN caida
                // en cada peticion; el TTL normal reintentara la BD despues.
                _huellasCargadoUtc = DateTime.UtcNow;
            }
            return;
        }

        lock (_cacheSync)
        {
            if (_huellas.Count > 0) return; // memoria vieja sigue siendo util
        }
        throw new HardwareDisconnectedException(
            "No se pudo cargar el padron de huellas: cberp inalcanzable y sin cache local.");
    }

    // -----------------------------------------------------------------
    // Captura asincrona (nunca .Wait()/.Result sobre el SDK)
    // -----------------------------------------------------------------
    private async Task<Sample> CapturarMuestraAsync(TimeSpan timeout, CancellationToken ct)
    {
        var handler = new CapturaSampleHandler();
        Capture? capture = null;

        try
        {
            // Con este SDK (1.6.1) no hay ReadersCollection: si no hay lector
            // el ctor o StartCapture lanzan SDKException -> 503.
            capture = new Capture();
            capture.EventHandler = handler;
            capture.StartCapture();
        }
        catch (Exception ex)
        {
            capture?.Dispose();
            _logger.LogWarning("No se pudo iniciar captura de huella: {Msg}", ex.Message);
            throw new HardwareDisconnectedException("Lector de huellas desconectado del puerto USB.");
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            return await handler.Task.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // cliente aborto la peticion
        }
        catch (OperationCanceledException)
        {
            throw new HardwareTimeoutException(
                $"No se detecto huella en {timeout.TotalSeconds:0} segundos.");
        }
        finally
        {
            // Limpieza best-effort: NUNCA debe lanzar ni enmascarar el resultado.
            try
            {
                capture?.StopCapture();
                if (capture is not null) capture.EventHandler = null;
                capture?.Dispose();
            }
            catch { /* el handle USB se libera con Dispose de todas formas */ }
        }
    }

    // Adapter DPFP.Capture.EventHandler -> TaskCompletionSource. La muestra
    // llega en el hilo del SDK; RunContinuationsAsynchronously evita deadlocks.
    private sealed class CapturaSampleHandler : DPFP.Capture.EventHandler
    {
        private readonly TaskCompletionSource<Sample> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Sample> Task => _tcs.Task;

        public void OnComplete(object capture, string readerSerialNumber, Sample sample)
            => _tcs.TrySetResult(sample);

        public void OnReaderDisconnect(object capture, string readerSerialNumber)
            => _tcs.TrySetException(new HardwareDisconnectedException(
                "El lector de huellas se desconecto durante la captura."));

        public void OnFingerGone(object capture, string readerSerialNumber) { }
        public void OnFingerTouch(object capture, string readerSerialNumber) { }
        public void OnReaderConnect(object capture, string readerSerialNumber) { }
        public void OnSampleQuality(object capture, string readerSerialNumber, CaptureFeedback feedback) { }
    }

    private static FeatureSet? ExtraerCaracteristicas(Sample sample, DataPurpose purpose)
    {
        var extractor = new FeatureExtraction();
        var feedback = CaptureFeedback.None;
        var features = new FeatureSet();
        try
        {
            extractor.CreateFeatureSet(sample, purpose, ref feedback, ref features);
        }
        catch (Exception ex) when (EsFalloMotorSdk(ex))
        {
            throw ErrorMotorSdk("extraccion de caracteristicas", ex);
        }
        return feedback == CaptureFeedback.Good ? features : null;
    }

    // Fallos del motor nativo DPFP (dpHMatch.dll / dpHFtrEx.dll / DPFPApi.dll
    // no instaladas, o SDKException del engine) => biometria no disponible.
    private static bool EsFalloMotorSdk(Exception ex)
        => ex is DllNotFoundException or TypeLoadException or BadImageFormatException
            or EntryPointNotFoundException or SDKException;

    private static HardwareDisconnectedException ErrorMotorSdk(string operacion, Exception ex)
        => new($"El motor de biometria ({operacion}) no esta disponible en este equipo: {ex.Message}");

    private static Template? CrearTemplate(byte[] bytes)
    {
        try { return new Template(new MemoryStream(bytes)); }
        catch { return null; }
    }

    // -----------------------------------------------------------------
    // cberp: cat_nomina_trabajadores.huella (varbinary(max), Template.Bytes)
    // -----------------------------------------------------------------
    private async Task<byte[]?> ObtenerHuellaEmpleadoAsync(string empleado, CancellationToken ct)
    {
        const string sql = """
            SELECT huella
            FROM dbo.cat_nomina_trabajadores WITH (NOLOCK)
            WHERE clave_empleado = @emp AND huella IS NOT NULL AND DATALENGTH(huella) > 0;
            """;
        try
        {
            await using var cn = await AbrirConexionAsync(ct);
            await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 10 };
            cmd.Parameters.AddWithValue("@emp", empleado);
            var r = await cmd.ExecuteScalarAsync(ct);
            return r as byte[]; // respuesta fresca de BD: null = sin huella -> 404
        }
        catch (Exception ex) when (ex is HardwareDisconnectedException or SqlException or TimeoutException)
        {
            _logger.LogWarning("cberp inalcanzable para huella de {Emp}: {Msg}. Usando cache local.",
                empleado, ex.Message);
        }

        // Respaldo WAN: el padron cacheado (memoria/archivo) cubre a los
        // empleados con huella; si no esta, se reporta 404 igualmente.
        await AsegurarCacheHuellasAsync(ct);
        lock (_cacheSync)
            return _huellas.TryGetValue(empleado, out var entrada) ? entrada.Bytes : null;
    }

    private async Task<Dictionary<string, EntradaHuella>> ConsultarHuellasBdAsync(CancellationToken ct)
    {
        // status = 1 -> personal activo (el historico/bajas vive en status 2).
        const string sql = """
            SELECT clave_empleado, huella
            FROM dbo.cat_nomina_trabajadores WITH (NOLOCK)
            WHERE status = 1 AND huella IS NOT NULL AND DATALENGTH(huella) > 0;
            """;
        var dict = new Dictionary<string, EntradaHuella>(StringComparer.OrdinalIgnoreCase);
        await using var cn = await AbrirConexionAsync(ct);
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 20 };
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var bytes = rd.GetValue(1) as byte[];
            if (bytes is { Length: > 0 })
                dict[rd.GetString(0)] = new EntradaHuella(bytes, DateTime.UtcNow);
        }
        return dict;
    }

    private async Task GuardarHuellaBdAsync(string empleado, byte[] templateBytes, CancellationToken ct)
    {
        // Mismo UPDATE que usa PruebaHuella.exe (GuardaHuella): el blob crudo
        // del template va en huella y se duplica en huella_foto.
        const string sql = """
            UPDATE dbo.cat_nomina_trabajadores
            SET huella = @huella, huella_foto = @huella
            WHERE clave_empleado = @emp;
            """;
        await using var cn = await AbrirConexionAsync(ct);
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 10 };
        cmd.Parameters.Add("@huella", System.Data.SqlDbType.VarBinary).Value = templateBytes;
        cmd.Parameters.AddWithValue("@emp", empleado);
        var filas = await cmd.ExecuteNonQueryAsync(ct);
        if (filas == 0)
            throw new HardwareNotFoundException($"El empleado '{empleado}' no existe en cat_nomina_trabajadores.");
    }

    private async Task<SqlConnection> AbrirConexionAsync(CancellationToken ct)
    {
        try
        {
            var cn = new SqlConnection(_connString);
            await cn.OpenAsync(ct);
            return cn;
        }
        catch (Exception ex) when (ex is SqlException or TimeoutException)
        {
            throw new HardwareDisconnectedException($"No se pudo conectar a cberp: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------
    // Cache local del padron (memoria + archivo JSON base64)
    // -----------------------------------------------------------------
    private async Task AsegurarCacheHuellasAsync(CancellationToken ct)
    {
        lock (_cacheSync)
        {
            if (_huellas.Count > 0 && DateTime.UtcNow - _huellasCargadoUtc < _cacheHuellas)
                return;
        }
        await RecargarCacheHuellasAsync(ct);
    }

    private void ActualizarCacheLocal(string empleado, byte[] templateBytes)
    {
        lock (_cacheSync)
        {
            if (_huellasCargadoUtc == DateTime.MinValue) return; // padron nunca cargado
            _huellas[empleado] = new EntradaHuella(templateBytes, DateTime.UtcNow);
        }
        _ = PersistirCacheArchivoAsync();
    }

    private async Task PersistirCacheArchivoAsync()
    {
        try
        {
            Dictionary<string, EntradaHuella> snapshot;
            lock (_cacheSync) snapshot = new Dictionary<string, EntradaHuella>(_huellas);

            var archivo = new CacheArchivo(
                DateTime.UtcNow,
                snapshot.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value.Bytes)));

            var dir = Path.GetDirectoryName(_archivoCacheLocal);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_archivoCacheLocal,
                JsonSerializer.Serialize(archivo));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("No se pudo persistir cache de huellas en {Ruta}: {Msg}",
                _archivoCacheLocal, ex.Message);
        }
    }

    private async Task<Dictionary<string, EntradaHuella>> LeerCacheArchivoAsync()
    {
        var dict = new Dictionary<string, EntradaHuella>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_archivoCacheLocal)) return dict;
            var json = await File.ReadAllTextAsync(_archivoCacheLocal);
            var archivo = JsonSerializer.Deserialize<CacheArchivo>(json);
            if (archivo?.Huellas is null) return dict;

            var cargado = archivo.ActualizadoUtc == default
                ? DateTime.UtcNow
                : DateTime.SpecifyKind(archivo.ActualizadoUtc, DateTimeKind.Utc);
            foreach (var (clave, b64) in archivo.Huellas)
            {
                try
                {
                    var bytes = Convert.FromBase64String(b64);
                    if (bytes.Length > 0)
                        dict[clave] = new EntradaHuella(bytes, cargado);
                }
                catch (FormatException) { /* entrada corrupta: se omite */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("No se pudo leer cache de huellas {Ruta}: {Msg}",
                _archivoCacheLocal, ex.Message);
        }
        return dict;
    }
}
