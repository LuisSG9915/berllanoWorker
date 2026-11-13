using System.Collections.Concurrent;
using System.Drawing.Printing;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using berllanoWorker.Exceptions;
using berllanoWorker.Interfaces;
using berllanoWorker.Models;
using Microsoft.Data.SqlClient;

namespace berllanoWorker.Services;

// Singleton: centraliza la resolucion multi-caja contra cberp
// (cat_RecursosWorker para impresoras/recursos, t_banorte_usuarios para
// terminales Banorte) con cache en memoria (CacheMinutos) + archivo JSON
// como respaldo si la WAN de la sucursal se cae.
public class ConfiguracionCajaService : IConfiguracionCajaService
{
    private readonly ILogger<ConfiguracionCajaService> _logger;
    private readonly string _connString;
    private readonly int _idEmpresa;
    private readonly int _sucursalDefault;
    private readonly int _cajaDefault;
    private readonly int _cacheMinutos;
    private readonly string _rutaCacheJson;

    // Credenciales de respaldo de ESTA pc (fallback de la caja default cuando
    // no hay fila en la tabla, p.ej. terminal/impresora de pruebas).
    private readonly string _ipTerminalLocal;
    private readonly string _puertoDefault;
    private readonly string _modoDefault;
    private readonly string _usuarioLocal;
    private readonly string _passwordLocal;
    private readonly string _afiliacionLocal;

    private sealed record FilaTerminal(string Usuario, string Password, string Afiliacion,
        string IdTerminal, string Puerto, string Modo);
    private sealed record EntradaCache(object? Valor, DateTime FechaUtc);
    private sealed record EntradaCacheArchivo(JsonElement? Valor, DateTime FechaUtc);
    private readonly ConcurrentDictionary<string, EntradaCache> _cache = new();

    // Claves de cache: "T|cia|suc|caja" = FilaTerminal|null (terminal),
    // "R|emp|suc|caja|recurso" = string nombreDispositivoLocal|null.
    public ConfiguracionCajaService(IConfiguration configuration, ILogger<ConfiguracionCajaService> logger)
    {
        _logger = logger;
        _connString = configuration.GetConnectionString("cberp") ?? string.Empty;
        // CajaLocal tiene prioridad; se aceptan las llaves viejas de
        // TerminalBanorte por compatibilidad con instalaciones existentes.
        _idEmpresa = configuration.GetValue("CajaLocal:IdEmpresaDefault",
            configuration.GetValue("TerminalBanorte:Cia", 1));
        _sucursalDefault = configuration.GetValue("CajaLocal:SucursalDefault",
            configuration.GetValue("TerminalBanorte:SucursalDefault", 1));
        _cajaDefault = configuration.GetValue("CajaLocal:CajaDefault",
            configuration.GetValue("TerminalBanorte:CajaDefault", 1));
        _cacheMinutos = configuration.GetValue("CajaLocal:CacheMinutos",
            configuration.GetValue("TerminalBanorte:CacheMinutos", 5));
        _rutaCacheJson = configuration.GetValue<string>("CajaLocal:RutaCacheJson")
            ?? configuration.GetValue<string>("TerminalBanorte:RutaCacheJson")
            ?? @"C:\berllanoWorker\data\config_cajas_cache.json";

        _ipTerminalLocal = configuration.GetValue<string>("TerminalBanorte:IpTerminal") ?? string.Empty;
        _puertoDefault = configuration.GetValue<string>("TerminalBanorte:PuertoDefault") ?? "1818";
        _modoDefault = configuration.GetValue<string>("TerminalBanorte:ModoDefault") ?? "AUT";
        _usuarioLocal = configuration.GetValue<string>("TerminalBanorte:Usuario") ?? string.Empty;
        _passwordLocal = configuration.GetValue<string>("TerminalBanorte:Password") ?? string.Empty;
        _afiliacionLocal = configuration.GetValue<string>("TerminalBanorte:Afiliacion") ?? string.Empty;
    }

    // -----------------------------------------------------------------
    // Impresoras / recursos (cat_RecursosWorker)
    // -----------------------------------------------------------------
    public async Task<string> ResolverImpresoraCajaAsync(int? sucursal, int? caja,
        string? recursoSolicitado, bool forzarRecarga = false, CancellationToken ct = default)
    {
        var recurso = recursoSolicitado?.Trim() ?? string.Empty;
        var esAlias = string.IsNullOrEmpty(recurso)
            || recurso.Equals("Tickets", StringComparison.OrdinalIgnoreCase)
            || recurso.Equals("Documentos", StringComparison.OrdinalIgnoreCase)
            || recurso.Equals("Default", StringComparison.OrdinalIgnoreCase);

        if (!esAlias && EsImpresoraInstalada(recurso))
            return recurso; // Regla A: nombre fisico directo

        var suc = sucursal ?? _sucursalDefault;
        var caj = caja ?? _cajaDefault;
        var recursoClave = esAlias
            ? (string.IsNullOrEmpty(recurso) ? "Tickets" : recurso)
            : recurso;

        var nombre = await ObtenerRecursoCajaAsync(suc, caj, recursoClave, forzarRecarga, ct);
        if (nombre is null)
            throw new HardwareValidationException(
                $"La Caja {caj} de la Sucursal {suc} no tiene una impresora asignada " +
                $"para el recurso '{recursoClave}' en cat_RecursosWorker.");

        return nombre;
    }

    private static bool EsImpresoraInstalada(string nombre)
        => PrinterSettings.InstalledPrinters.Cast<string>()
            .Any(p => p.Equals(nombre, StringComparison.OrdinalIgnoreCase));

    private async Task<string?> ObtenerRecursoCajaAsync(int suc, int caj, string recurso,
        bool forzarRecarga, CancellationToken ct)
    {
        var key = $"R|{_idEmpresa}|{suc}|{caj}|{recurso}";
        return await ObtenerCacheadoAsync<string?>(key, forzarRecarga, async token =>
        {
            const string sql = """
                SELECT TOP 1 LTRIM(RTRIM(NombreDispositivoLocal))
                FROM dbo.cat_RecursosWorker WITH (NOLOCK)
                WHERE IdEmpresa = @emp AND IdSucursal = @suc AND Caja = @caj
                  AND Recurso = @recurso AND Activo = 1;
                """;
            await using var cn = new SqlConnection(_connString);
            await cn.OpenAsync(token);
            await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 10 };
            cmd.Parameters.AddWithValue("@emp", _idEmpresa);
            cmd.Parameters.AddWithValue("@suc", suc);
            cmd.Parameters.AddWithValue("@caj", caj);
            cmd.Parameters.AddWithValue("@recurso", recurso);
            var r = await cmd.ExecuteScalarAsync(token);
            return r as string;
        });
    }

    // -----------------------------------------------------------------
    // Terminal Banorte (t_banorte_usuarios)
    // -----------------------------------------------------------------
    public async Task<TerminalCredencialesDto> ResolverTerminalCajaAsync(
        TerminalConfigOverrideDto request, CancellationToken ct = default)
    {
        var suc = request.Sucursal ?? _sucursalDefault;
        var caj = request.Caja ?? _cajaDefault;
        var esCajaLocal = suc == _sucursalDefault && caj == _cajaDefault;

        var fila = await ObtenerFilaTerminalAsync(suc, caj, request.ForzarRecarga, ct);
        var origen = fila is not null ? "DB" : "local";

        // La IP del pinpad puede vivir como recurso 'TerminalBanorte' en
        // cat_RecursosWorker (NombreDispositivoLocal = ip), opcional.
        var ipRecurso = await ObtenerRecursoCajaAsync(suc, caj, "TerminalBanorte",
            request.ForzarRecarga, ct);

        var usuario = PrimeroNoVacio(request.Usuario, fila?.Usuario,
            esCajaLocal ? _usuarioLocal : string.Empty);
        var password = PrimeroNoVacio(request.Password, fila?.Password,
            esCajaLocal ? _passwordLocal : string.Empty);

        if (fila is null && !esCajaLocal
            && (string.IsNullOrWhiteSpace(usuario) || string.IsNullOrWhiteSpace(password)))
            throw new HardwareValidationException(
                $"La Caja {caj} de la Sucursal {suc} no tiene terminal configurada " +
                "en t_banorte_usuarios.");

        var ip = PrimeroNoVacio(request.IpTerminal, ipRecurso, _ipTerminalLocal);
        var puertoStr = PrimeroNoVacio(request.Puerto, fila?.Puerto, _puertoDefault);
        var modo = PrimeroNoVacio(request.Modo, fila?.Modo, _modoDefault);
        var afiliacion = PrimeroNoVacio(request.Afiliacion, fila?.Afiliacion,
            esCajaLocal ? _afiliacionLocal : string.Empty);

        if (request.IpTerminal is not null || request.Usuario is not null
            || request.Password is not null || request.Modo is not null || request.Afiliacion is not null)
            origen += "+override";

        if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(usuario) || string.IsNullOrWhiteSpace(password))
            throw new HardwareValidationException(
                "Configuracion de terminal incompleta: IpTerminal, Usuario y Password son obligatorios " +
                "(fila t_banorte_usuarios, recurso 'TerminalBanorte' en cat_RecursosWorker, " +
                "seccion TerminalBanorte en appsettings.json u overrides en el request).");

        if (!int.TryParse(puertoStr, NumberStyles.None, CultureInfo.InvariantCulture, out var puerto)
            || puerto <= 0 || puerto > 65535)
            throw new HardwareValidationException($"Puerto de terminal invalido: '{puertoStr}'.");

        return new TerminalCredencialesDto
        {
            IpTerminal = ip, Puerto = puerto, PuertoStr = puertoStr,
            Usuario = usuario, Password = password, Modo = modo,
            Afiliacion = afiliacion, IdTerminal = fila?.IdTerminal ?? string.Empty,
            Sucursal = suc, Caja = caj, Origen = origen
        };
    }

    private async Task<FilaTerminal?> ObtenerFilaTerminalAsync(int suc, int caj,
        bool forzarRecarga, CancellationToken ct)
    {
        var key = $"T|{_idEmpresa}|{suc}|{caj}";
        return await ObtenerCacheadoAsync<FilaTerminal?>(key, forzarRecarga, async token =>
        {
            const string sql = """
                SELECT TOP 1
                    LTRIM(RTRIM(ISNULL(usr_banorte, '')))   AS usr_banorte,
                    LTRIM(RTRIM(ISNULL(pass_banorte, '')))  AS pass_banorte,
                    LTRIM(RTRIM(ISNULL(id_afiliacion, ''))) AS id_afiliacion,
                    LTRIM(RTRIM(ISNULL(id_terminal, '')))   AS id_terminal,
                    LTRIM(RTRIM(ISNULL(puerto, '1818')))    AS puerto,
                    LTRIM(RTRIM(ISNULL(modo, 'AUT')))       AS modo
                FROM dbo.t_banorte_usuarios WITH (NOLOCK)
                WHERE cia = @emp AND sucursal = @suc AND caja = @caj;
                """;
            await using var cn = new SqlConnection(_connString);
            await cn.OpenAsync(token);
            await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 10 };
            cmd.Parameters.AddWithValue("@emp", _idEmpresa);
            cmd.Parameters.AddWithValue("@suc", suc);
            cmd.Parameters.AddWithValue("@caj", caj);
            await using var rd = await cmd.ExecuteReaderAsync(token);
            if (!await rd.ReadAsync(token)) return null;
            return new FilaTerminal(
                rd.GetString(0), rd.GetString(1), rd.GetString(2),
                rd.GetString(3), rd.GetString(4), rd.GetString(5));
        });
    }

    // -----------------------------------------------------------------
    // Listados (sin pass_banorte)
    // -----------------------------------------------------------------
    public async Task<List<TerminalInfoDto>> ObtenerTerminalesConEstadoAsync(int? sucursal, bool verificarPing, CancellationToken ct = default)
    {
        var lista = new List<TerminalInfoDto>();
        if (string.IsNullOrWhiteSpace(_connString)) return lista;

        var sql = """
            SELECT cia, sucursal, caja,
                LTRIM(RTRIM(ISNULL(id_terminal, '')))   AS id_terminal,
                LTRIM(RTRIM(ISNULL(id_afiliacion, ''))) AS id_afiliacion,
                LTRIM(RTRIM(ISNULL(usr_banorte, '')))   AS usr_banorte,
                LTRIM(RTRIM(ISNULL(puerto, '1818')))    AS puerto,
                LTRIM(RTRIM(ISNULL(modo, 'AUT')))       AS modo
            FROM dbo.t_banorte_usuarios WITH (NOLOCK)
            WHERE cia = @emp
            """ + (sucursal.HasValue ? " AND sucursal = @suc" : "") +
            " ORDER BY sucursal, caja;";

        await using var cn = await AbrirConexionAsync(ct);
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 10 };
        cmd.Parameters.AddWithValue("@emp", _idEmpresa);
        if (sucursal.HasValue) cmd.Parameters.AddWithValue("@suc", sucursal.Value);

        var terminales = new List<(int Sucursal, int Caja, string IpTerminal, string Puerto, string Afiliacion, string Usuario, string Modo)>();
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                terminales.Add((
                    rd.GetInt16(1), rd.GetInt16(2),
                    rd.GetString(3), rd.GetString(6), rd.GetString(4), rd.GetString(5), rd.GetString(7)
                ));
            }
        }

        // Obtener IPs de cat_RecursosWorker para cada terminal
        var sqlRecursos = """
            SELECT IdSucursal, Caja, LTRIM(RTRIM(NombreDispositivoLocal)) AS ip
            FROM dbo.cat_RecursosWorker WITH (NOLOCK)
            WHERE IdEmpresa = @emp AND Recurso = 'TerminalBanorte' AND Activo = 1
            """ + (sucursal.HasValue ? " AND IdSucursal = @suc" : "") + ";";

        var ipsPorCaja = new Dictionary<(int, int), string>();
        await using (var cmdRec = new SqlCommand(sqlRecursos, cn) { CommandTimeout = 10 })
        {
            cmdRec.Parameters.AddWithValue("@emp", _idEmpresa);
            if (sucursal.HasValue) cmdRec.Parameters.AddWithValue("@suc", sucursal.Value);
            await using var rdRec = await cmdRec.ExecuteReaderAsync(ct);
            while (await rdRec.ReadAsync(ct))
            {
                ipsPorCaja[(rdRec.GetInt16(0), rdRec.GetInt16(1))] = rdRec.GetString(2);
            }
        }

        // Verificar ping TCP en paralelo si se solicita
        var tareas = new List<Task<TerminalInfoDto>>();
        foreach (var t in terminales)
        {
            var ip = ipsPorCaja.GetValueOrDefault((t.Sucursal, t.Caja), _ipTerminalLocal);
            var puerto = int.TryParse(t.Puerto, out var p) ? p : 1818;

            tareas.Add(Task.Run(async () =>
            {
                var online = false;
                if (verificarPing && !string.IsNullOrWhiteSpace(ip))
                {
                    try
                    {
                        using var tcp = new TcpClient();
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(800));
                        await tcp.ConnectAsync(ip, puerto, timeoutCts.Token);
                        online = true;
                    }
                    catch
                    {
                        online = false;
                    }
                }

                return new TerminalInfoDto
                {
                    Sucursal = t.Sucursal,
                    Caja = t.Caja,
                    IpTerminal = ip,
                    Puerto = t.Puerto,
                    Afiliacion = t.Afiliacion,
                    Usuario = t.Usuario,
                    Modo = t.Modo,
                    Online = online
                };
            }, ct));
        }

        var resultados = await Task.WhenAll(tareas);
        lista.AddRange(resultados);
        return lista;
    }

    public async Task<List<TerminalCajaDto>> ObtenerCajasTerminalAsync(int? sucursal, CancellationToken ct = default)
    {
        var lista = new List<TerminalCajaDto>();
        if (string.IsNullOrWhiteSpace(_connString)) return lista;

        var sql = """
            SELECT cia, sucursal, caja,
                LTRIM(RTRIM(ISNULL(id_terminal, '')))   AS id_terminal,
                LTRIM(RTRIM(ISNULL(id_afiliacion, ''))) AS id_afiliacion,
                LTRIM(RTRIM(ISNULL(usr_banorte, '')))   AS usr_banorte,
                LTRIM(RTRIM(ISNULL(puerto, '1818')))    AS puerto,
                LTRIM(RTRIM(ISNULL(modo, 'AUT')))       AS modo
            FROM dbo.t_banorte_usuarios WITH (NOLOCK)
            WHERE cia = @emp
            """ + (sucursal.HasValue ? " AND sucursal = @suc" : "") +
            " ORDER BY sucursal, caja;";

        await using var cn = await AbrirConexionAsync(ct);
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 10 };
        cmd.Parameters.AddWithValue("@emp", _idEmpresa);
        if (sucursal.HasValue) cmd.Parameters.AddWithValue("@suc", sucursal.Value);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            lista.Add(new TerminalCajaDto
            {
                Cia = rd.GetInt16(0), Sucursal = rd.GetInt16(1), Caja = rd.GetInt16(2),
                IdTerminal = rd.GetString(3), Afiliacion = rd.GetString(4),
                Usuario = rd.GetString(5), Puerto = rd.GetString(6),
                Modo = rd.GetString(7), Origen = "DB"
            });
        }
        return lista;
    }

    public async Task<List<CajaResumenDto>> ObtenerResumenCajasAsync(int? sucursal, CancellationToken ct = default)
    {
        var resumen = new Dictionary<int, CajaResumenDto>();
        if (string.IsNullOrWhiteSpace(_connString)) return new();

        var sqlRecursos = """
            SELECT Caja, Recurso, LTRIM(RTRIM(NombreDispositivoLocal)) AS nombre
            FROM dbo.cat_RecursosWorker WITH (NOLOCK)
            WHERE IdEmpresa = @emp AND Activo = 1
            """ + (sucursal.HasValue ? " AND IdSucursal = @suc" : "") + ";";

        var instaladas = PrinterSettings.InstalledPrinters.Cast<string>().ToList();

        await using var cn = await AbrirConexionAsync(ct);

        await using (var cmd = new SqlCommand(sqlRecursos, cn) { CommandTimeout = 10 })
        {
            cmd.Parameters.AddWithValue("@emp", _idEmpresa);
            if (sucursal.HasValue) cmd.Parameters.AddWithValue("@suc", sucursal.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var caj = Convert.ToInt32(rd.GetValue(0));
                var recurso = rd.GetString(1);
                var nombre = rd.GetString(2);
                var entrada = resumen.TryGetValue(caj, out var e) ? e : resumen[caj] = new CajaResumenDto { Caja = caj };
                if (recurso.Equals("TerminalBanorte", StringComparison.OrdinalIgnoreCase))
                    entrada.IpPinpad = nombre;
                else
                    entrada.Impresoras.Add(new RecursoCajaDto
                    {
                        Recurso = recurso,
                        NombreDispositivoLocal = nombre,
                        InstaladaLocal = instaladas.Any(p => p.Equals(nombre, StringComparison.OrdinalIgnoreCase))
                    });
            }
        }

        var terminales = await ObtenerCajasTerminalAsync(sucursal, ct);
        foreach (var t in terminales)
        {
            var entrada = resumen.TryGetValue(t.Caja, out var e) ? e : resumen[t.Caja] = new CajaResumenDto { Caja = t.Caja };
            entrada.Terminal = t;
        }

        return resumen.Values.OrderBy(x => x.Caja).ToList();
    }

    // -----------------------------------------------------------------
    // Cache + persistencia + helpers
    // -----------------------------------------------------------------
    // Cachea por clave tanto "encontrado" como "no encontrado" (Valor=null)
    // para no golpear la WAN en cada peticion cuando la fila no existe.
    private async Task<T> ObtenerCacheadoAsync<T>(string key, bool forzarRecarga,
        Func<CancellationToken, Task<T>> consulta)
    {
        if (!forzarRecarga
            && _cache.TryGetValue(key, out var vigente)
            && DateTime.UtcNow - vigente.FechaUtc < TimeSpan.FromMinutes(_cacheMinutos))
            return (T)vigente.Valor!;

        if (string.IsNullOrWhiteSpace(_connString))
            return await LeerCacheArchivoAsync<T>(key);

        try
        {
            var valor = await consulta(default);
            _cache[key] = new EntradaCache(valor, DateTime.UtcNow);
            _ = PersistirCacheArchivoAsync(); // best-effort, no bloqueante
            return valor;
        }
        catch (Exception ex) when (ex is SqlException or TimeoutException)
        {
            _logger.LogWarning("cberp inalcanzable ({Key}): {Msg}. Usando cache/archivo.", key, ex.Message);
            if (_cache.TryGetValue(key, out var vieja))
                return (T)vieja.Valor!;
            return await LeerCacheArchivoAsync<T>(key);
        }
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

    private async Task PersistirCacheArchivoAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(_rutaCacheJson);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_rutaCacheJson, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("No se pudo persistir cache de cajas en {Ruta}: {Msg}", _rutaCacheJson, ex.Message);
        }
    }

    // El Valor llega como JsonElement desde el archivo; se materializa a T.
    private async Task<T> LeerCacheArchivoAsync<T>(string key)
    {
        try
        {
            if (!File.Exists(_rutaCacheJson)) return default!;
            var json = await File.ReadAllTextAsync(_rutaCacheJson);
            var dict = JsonSerializer.Deserialize<Dictionary<string, EntradaCacheArchivo>>(json);
            if (dict is null || !dict.TryGetValue(key, out var e) || e.Valor is null)
                return default!;
            return e.Valor.Value.Deserialize<T>()!;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("No se pudo leer cache de cajas {Ruta}: {Msg}", _rutaCacheJson, ex.Message);
            return default!;
        }
    }

    private static string PrimeroNoVacio(string? primero, string? segundo, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(primero)) return primero.Trim();
        if (!string.IsNullOrWhiteSpace(segundo)) return segundo.Trim();
        return fallback;
    }

    public string ObtenerConnectionString() => _connString;
}
