using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using berllanoWorker.Exceptions;
using berllanoWorker.Interfaces;
using berllanoWorker.Models;

namespace berllanoWorker.Services;

// Singleton: el pinpad Banorte/Payworks es un recurso exclusivo por LAN (un
// socket TCP a la vez). Un solo Semaforo(1,1) serializa las 5 operaciones;
// el wrapper COM se invoca fuera de proceso via BanorteWorker.exe.
public class TerminalService : ITerminalService
{
    private readonly SemaphoreSlim _pinpadLock = new(1, 1);
    private readonly ILogger<TerminalService> _logger;

    private readonly string _rutaWorkerExe;
    private readonly string _rutaWrapperDll;
    private readonly string _ipTerminal;
    private readonly string _puerto;
    private readonly string _usuario;
    private readonly string _password;
    private readonly string _afiliacion;
    private readonly string _modo;
    private readonly int _timeoutSegundos;
    private readonly bool _validarPingTcpPrevio;

    private sealed record ConfigEfectiva(string Ip, int Puerto, string PuertoStr,
        string Usuario, string Password, string Modo, string Afiliacion);

    public TerminalService(IConfiguration configuration, ILogger<TerminalService> logger)
    {
        _logger = logger;
        _rutaWorkerExe = configuration.GetValue<string>("TerminalBanorte:RutaWorkerExe")
            ?? @"C:\asesor\BanortePOS\BanorteWorker.exe";
        _rutaWrapperDll = configuration.GetValue<string>("TerminalBanorte:RutaWrapperDll")
            ?? @"C:\asesor\BanortePOS\BanortePayworksWrapper.dll";
        _ipTerminal = configuration.GetValue<string>("TerminalBanorte:IpTerminal") ?? string.Empty;
        _puerto = configuration.GetValue<string>("TerminalBanorte:Puerto") ?? "1818";
        _usuario = configuration.GetValue<string>("TerminalBanorte:Usuario") ?? string.Empty;
        _password = configuration.GetValue<string>("TerminalBanorte:Password") ?? string.Empty;
        _afiliacion = configuration.GetValue<string>("TerminalBanorte:Afiliacion") ?? string.Empty;
        _modo = configuration.GetValue<string>("TerminalBanorte:Modo") ?? "AUT";
        _timeoutSegundos = configuration.GetValue("TerminalBanorte:TimeoutSegundos", 150);
        _validarPingTcpPrevio = configuration.GetValue("TerminalBanorte:ValidarPingTcpPrevio", true);

        // El exe escribe su traza siempre en esta ruta fija (hardcodeada en
        // su codigo). Creamos la carpeta por adelantado para cajas nuevas.
        try
        {
            Directory.CreateDirectory(@"C:\asesor\BanortePOS\Logs");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("No se pudo crear C:\\asesor\\BanortePOS\\Logs: {Msg}", ex.Message);
        }
    }

    public Task<TerminalTransaccionResponseDto> InicializarLlavesAsync(TerminalLlavesRequestDto request, CancellationToken ct = default)
    {
        var cfg = ResolverConfig(request);
        var ahora = DateTime.Now;
        return EjecutarAsync(cfg, "CARGAR_LLAVE",
            numControl: "KEY" + ahora.ToString("yyyyMMddHHmmss"),
            uuid: "UUID-KEY-" + ahora.ToString("yyyyMMddHHmmss"),
            monto: 0.00m, efectivo: 0.00m, ct);
    }

    public async Task<TerminalTransaccionResponseDto> CobrarAsync(TerminalCobroRequestDto request, CancellationToken ct = default)
    {
        ValidarMonto(request.Monto);

        var cfg = ResolverConfig(request);
        var ahora = DateTime.Now;
        var dto = await EjecutarAsync(cfg, "VENTA",
            numControl: ahora.ToString("ddHHmmss") + "B",
            uuid: "UUID-" + ahora.ToString("yyyyMMddHHmmss"),
            monto: request.Monto, efectivo: 0.00m, ct);
        dto.ReferenciaCliente = request.Referencia;
        return dto;
    }

    public Task<TerminalTransaccionResponseDto> RetirarEfectivoAsync(TerminalRetiroRequestDto request, CancellationToken ct = default)
    {
        ValidarMonto(request.Monto);

        var cfg = ResolverConfig(request);
        var ahora = DateTime.Now;
        return EjecutarAsync(cfg, "CASHBACK",
            numControl: ahora.ToString("ddHHmmss") + "C",
            uuid: "UUID-" + ahora.ToString("yyyyMMddHHmmss") + "-C",
            monto: request.MontoVenta > 0 ? request.MontoVenta : request.Monto,
            efectivo: request.Monto, ct);
    }

    // numControl SIEMPRE lo genera el worker (ddHHmmss + sufijo): una misma
    // venta POS puede pagarse con varias tarjetas o reintentarse, y Payworks
    // rechaza numControl duplicado y guiones.
    private static void ValidarMonto(decimal monto)
    {
        // Techo anti-captura erronea (ej. escanear un barcode en el input) y
        // contra la perdida de precision del ToSingle (float32) del exe.
        if (monto <= 0 || monto > 999999.99m)
            throw new HardwareValidationException(
                "El monto debe ser mayor a $0.00 y no exceder $999,999.99.");
    }

    public Task<TerminalTransaccionResponseDto> CancelarTransaccionAsync(TerminalCancelacionRequestDto request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.TransaccionId))
            throw new HardwareValidationException("El campo 'transaccion_id' es obligatorio para cancelar.");

        var cfg = ResolverConfig(request);
        var id = request.TransaccionId.Trim();

        // Devolucion financiera real: solo si el id es una Referencia
        // Payworks (numerica de 12 digitos) y llega el monto a reversar.
        if (EsReferenciaPayworks(id) && request.Monto.GetValueOrDefault() > 0)
        {
            ValidarMonto(request.Monto!.Value);
            return ConBloqueoAsync(cfg,
                token => EjecutarDevolucionWrapperAsync(cfg, id, request.Monto.Value, token), ct);
        }

        // Caso general: reset de sesion EMV (CANCELAR / PPSendTxn(3)).
        return EjecutarAsync(cfg, "CANCELAR",
            numControl: id,
            uuid: "UUID-CAN-" + DateTime.Now.ToString("yyyyMMddHHmmss"),
            monto: 0.00m, efectivo: 0.00m, ct);
    }

    private static bool EsReferenciaPayworks(string id)
        => id.Length == 12 && id.All(char.IsDigit);

    public Task<TerminalTransaccionResponseDto> VerificarTransaccionAsync(TerminalVerificarRequestDto request, CancellationToken ct = default)
    {
        // Equivalente a la validacion numControlOriginal = "" del VBA
        if (string.IsNullOrWhiteSpace(request.NumeroControl))
            throw new HardwareValidationException("El campo 'numero_control' es obligatorio para verificar.");

        var cfg = ResolverConfig(request);
        return EjecutarAsync(cfg, "VERIFICACION",
            numControl: request.NumeroControl.Trim(),
            uuid: "UUID-VER-" + DateTime.Now.ToString("yyyyMMddHHmmss"),
            monto: 0.00m, efectivo: 0.00m, ct);
    }

    // Combina overrides del request con los defaults de TerminalBanorte.
    // Ip/Usuario/Password vacios -> 422 (igual que Inicializar() del VBA).
    private ConfigEfectiva ResolverConfig(TerminalConfigOverrideDto? o)
    {
        var ip = PrimeroNoVacio(o?.IpTerminal, _ipTerminal);
        var puertoStr = PrimeroNoVacio(o?.Puerto, _puerto);
        var usuario = PrimeroNoVacio(o?.Usuario, _usuario);
        var password = PrimeroNoVacio(o?.Password, _password);
        var modo = PrimeroNoVacio(o?.Modo, _modo);
        var afiliacion = PrimeroNoVacio(o?.Afiliacion, _afiliacion);

        if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(usuario) || string.IsNullOrWhiteSpace(password))
            throw new HardwareValidationException(
                "Configuracion de terminal incompleta: IpTerminal, Usuario y Password son obligatorios " +
                "(seccion TerminalBanorte en appsettings.json o overrides en el request).");

        if (!int.TryParse(puertoStr, NumberStyles.None, CultureInfo.InvariantCulture, out var puerto) || puerto <= 0 || puerto > 65535)
            throw new HardwareValidationException($"Puerto de terminal invalido: '{puertoStr}'.");

        return new ConfigEfectiva(ip, puerto, puertoStr, usuario, password, modo, afiliacion);
    }

    private static string PrimeroNoVacio(string? primero, string fallback)
        => string.IsNullOrWhiteSpace(primero) ? fallback : primero.Trim();

    private Task<TerminalTransaccionResponseDto> EjecutarAsync(
        ConfigEfectiva cfg, string operacion, string numControl, string uuid,
        decimal monto, decimal efectivo, CancellationToken ct)
        => ConBloqueoAsync(cfg, async token =>
        {
            if (!File.Exists(_rutaWorkerExe))
                throw new HardwareDisconnectedException(
                    $"No se encontro BanorteWorker.exe en '{_rutaWorkerExe}'. " +
                    "Verifica la ruta TerminalBanorte:RutaWorkerExe y la instalacion del wrapper.");

            // El exe parsea monto/efectivo con Conversions.ToSingle -> usa la
            // cultura AMBIENTAL de Windows (en esta flota: coma decimal). El
            // worker y el exe corren en la misma PC, asi que formatear con la
            // cultura local garantiza que ambos interpreten el mismo numero.
            // Con InvariantCulture ("1.00") el exe leia 100 en PCs con coma.
            var args = $"\"{operacion}\" \"{cfg.Ip}\" \"{cfg.PuertoStr}\" \"{cfg.Usuario}\" \"{cfg.Password}\" " +
                       $"\"{monto.ToString("0.00")}\" \"{numControl}\" \"{cfg.Modo}\" " +
                       $"\"{uuid}\" \"{efectivo.ToString("0.00")}\"";

            _logger.LogInformation(
                "Terminal {Operacion} -> {Ip}:{Puerto} numControl={NumControl} monto={Monto} efectivo={Efectivo} modo={Modo}",
                operacion, cfg.Ip, cfg.PuertoStr, numControl, monto, efectivo, cfg.Modo);

            var raw = await EjecutarWorkerAsync(args, token);
            var dto = ParsearRespuesta(raw);

            dto.NumControl = numControl;
            dto.UuidSesion = uuid;
            if (dto.Monto == 0) dto.Monto = monto;
            dto.MontoEfectivo = efectivo;
            return dto;
        }, ct);

    // Reversa financiera real (DEVOLUCION): el exe actual no la expone en su
    // switch, pero BanortePayworksWrapper.dll si la implementa
    // (PPSetCommand("DEVOLUCION") + PPSetPaywReference + PPSendTxn(30)).
    // Se carga por reflection sin requerir registro COM.
    private async Task<TerminalTransaccionResponseDto> EjecutarDevolucionWrapperAsync(
        ConfigEfectiva cfg, string referencia, decimal monto, CancellationToken ct)
    {
        if (!File.Exists(_rutaWrapperDll))
            throw new HardwareDisconnectedException(
                $"La devolucion financiera requiere BanortePayworksWrapper.dll en '{_rutaWrapperDll}' " +
                "(TerminalBanorte:RutaWrapperDll). Sin la DLL solo se admite CANCELAR (reset de sesion).");

        var dir = Path.GetDirectoryName(_rutaWrapperDll);
        if (!string.IsNullOrEmpty(dir))
            SetDllDirectory(dir); // para que el P/Invoke encuentre PINPAD.TPV junto a la DLL

        var numControl = DateTime.Now.ToString("ddHHmmss") + "D";
        var uuid = "UUID-DEV-" + DateTime.Now.ToString("yyyyMMddHHmmss");

        var tarea = Task.Run(() =>
        {
            try
            {
                var asm = Assembly.LoadFrom(_rutaWrapperDll);
                var tipo = asm.GetType("BanortePayworks.PinpadWrapper")
                    ?? throw new InvalidOperationException("Tipo PinpadWrapper no encontrado en la DLL.");
                var instancia = Activator.CreateInstance(tipo)
                    ?? throw new InvalidOperationException("No se pudo instanciar PinpadWrapper.");
                var metodo = tipo.GetMethod("EjecutarDevolucion")
                    ?? throw new InvalidOperationException("EjecutarDevolucion no existe en la DLL.");

                var raw = metodo.Invoke(instancia, new object?[] {
                    cfg.Ip, cfg.PuertoStr, cfg.Usuario, cfg.Password,
                    (float)monto, referencia, numControl, cfg.Modo, uuid }) as string;

                if (string.IsNullOrWhiteSpace(raw))
                    throw new InvalidOperationException("EjecutarDevolucion devolvio respuesta vacia.");

                return ParsearRespuesta(raw);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                throw new HardwareDisconnectedException(
                    $"El wrapper fallo ejecutando DEVOLUCION: {tie.InnerException.Message}");
            }
        }, ct);

        var ganador = await Task.WhenAny(tarea, Task.Delay(TimeSpan.FromSeconds(_timeoutSegundos), ct));
        if (ganador != tarea)
        {
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            throw new HardwareTimeoutException(
                $"DEVOLUCION excedio el timeout de {_timeoutSegundos}s esperando al wrapper.");
        }

        var dto = await tarea;
        dto.NumControl = numControl;
        dto.UuidSesion = uuid;
        if (dto.Monto == 0) dto.Monto = monto;
        return dto;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    // Semaforo exclusivo del pinpad + chequeo TCP previo para cualquier
    // operacion que hable con la terminal (exe o wrapper in-proc).
    private async Task<TerminalTransaccionResponseDto> ConBloqueoAsync(
        ConfigEfectiva cfg, Func<CancellationToken, Task<TerminalTransaccionResponseDto>> accion, CancellationToken ct)
    {
        // Rechazo inmediato con 423 si el pinpad ya esta en uso.
        if (!await _pinpadLock.WaitAsync(0, ct))
            throw new HardwareBusyException(
                "La terminal Banorte esta procesando otra operacion. Reintenta en unos segundos.");

        try
        {
            if (_validarPingTcpPrevio)
                await ValidarConexionTcpAsync(cfg.Ip, cfg.Puerto, ct);

            return await accion(ct);
        }
        finally
        {
            // Pausa deliberada antes de liberar: da tiempo a que el socket TCP
            // de la terminal quede libre para la siguiente operacion.
            await Task.Delay(500);
            _pinpadLock.Release();
        }
    }

    private async Task ValidarConexionTcpAsync(string ip, int puerto, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(ip, puerto, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HardwareDisconnectedException(
                $"La terminal {ip}:{puerto} no respondio al chequeo TCP (3s). " +
                "Verifica que este encendida y en la misma red.");
        }
        catch (SocketException ex)
        {
            throw new HardwareDisconnectedException(
                $"Sin conexion TCP con la terminal {ip}:{puerto}: {ex.SocketErrorCode}. " +
                "Verifica que este encendida y en la misma red.");
        }
    }

    private async Task<string> EjecutarWorkerAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _rutaWorkerExe,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new HardwareDisconnectedException($"No se pudo iniciar BanorteWorker.exe: {ex.Message}");
        }

        if (proc is null)
            throw new HardwareDisconnectedException("No se pudo iniciar BanorteWorker.exe.");

        using (proc)
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSegundos));
            try
            {
                await proc.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* no-op */ }
                throw new HardwareTimeoutException(
                    $"BanorteWorker.exe excedio el timeout de {_timeoutSegundos}s y fue terminado. " +
                    "La terminal pudo quedar esperando interaccion en el pinpad.");
            }

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
                throw new HardwareDisconnectedException(
                    $"BanorteWorker.exe termino con codigo {proc.ExitCode} sin respuesta. {stderr}");

            if (string.IsNullOrWhiteSpace(stdout))
                throw new HardwareDisconnectedException("BanorteWorker.exe no devolvio ninguna respuesta.");

            return stdout;
        }
    }

    // Formato estandar del wrapper (clsBanorteManager.ParsearRespuesta):
    //   Estado|Codigo|Msg|Auth|Ref|Voucher|BIN|PAN|Entry|PIN|Tipo|Marca|Banco|Exp|AID|TVR|PrefName|Label
    // con la traza de ejecucion separada por "||TRACE||".
    // VERIFICACION puede responder el formato corto VTA|Referencia|Tarjeta|Monto|Estatus.
    private TerminalTransaccionResponseDto ParsearRespuesta(string rawResponse)
    {
        var dto = new TerminalTransaccionResponseDto { RawResponse = rawResponse };

        var bloques = rawResponse.Split("||TRACE||");
        dto.UltimaTraza = bloques.Length > 1 && !string.IsNullOrWhiteSpace(bloques[1])
            ? bloques[1].Trim()
            : "SIN_TRAZA";

        var campos = bloques[0].Trim().Split('|');
        var estado = campos[0].Trim().ToUpperInvariant();

        // CANCELAR del exe regresa literal "OK|0|CANCELADO|..." (no consulta
        // a la terminal, solo libera la sesion EMV con PPSendTxn(3)).
        if (estado == "OK")
        {
            dto.Estado = "OK";
            dto.Aprobada = true;
            dto.CodigoRespuesta = Campo(campos, 1);
            dto.MensajeRespuesta = Campo(campos, 2);
            return dto;
        }

        // VERIFICACION exitosa: campos[2] trae el detalle Payworks
        // "VTA%7C<ref>%7C<tarjeta>%7C<monto>%7C..." URL-encodeado (la DLL lo
        // entrega asi en PPVBGetText; LimpiarCadena no toca el %7C).
        var mensaje = Campo(campos, 2);
        if (estado == "VTA" || mensaje.StartsWith("VTA", StringComparison.OrdinalIgnoreCase))
        {
            // Layout Payworks VTA (pos. 0-7 fijas):
            // 0=VTA 1=RefPayworks 2=TarjetaMask 3=Monto 4=Tipo(C/V=venta,
            // X=cancelacion, D=devolucion) 5=Estatus 6=Codigo 7=Auth
            // 8-11=timestamps. Si >=19 campos: 12=Banco 13=Marca 14=TipoT
            // 16=AID 17=PanMask. Si <19: AID=[^3] PanMask=[^2]. [^1]=NumControl.
            var vta = Uri.UnescapeDataString(estado == "VTA" ? bloques[0].Trim() : mensaje).Split('|');
            var estatus = Campo(vta, 5).ToUpperInvariant();
            var codigo = Campo(vta, 6);

            dto.Estado = "VTA";
            dto.Aprobada = estatus == "A";
            dto.CodigoRespuesta = dto.Aprobada && string.IsNullOrWhiteSpace(codigo) ? "00" : codigo;
            dto.CodigoAutorizacion = Campo(vta, 7);
            dto.MensajeRespuesta = estatus switch
            {
                "A" => "Aplicada",
                "D" => $"Declinada{(string.IsNullOrWhiteSpace(codigo) ? "" : $" (codigo {codigo})")}",
                "R" => $"Rechazada{(string.IsNullOrWhiteSpace(codigo) ? "" : $" (codigo {codigo})")}",
                "C" => "Cancelada",
                _ => $"No aplicada (estatus {estatus})"
            };
            if (vta.Length > 1) dto.ReferenciaPayworks = vta[1].Trim();
            if (vta.Length > 2) dto.TarjetaEnmascarada = vta[2].Trim();
            if (vta.Length > 3 && decimal.TryParse(vta[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var montoVta))
                dto.Monto = montoVta;
            if (vta.Length >= 19)
            {
                dto.BancoEmisor = vta[12].Trim();
                dto.MarcaTarjeta = vta[13].Trim();
                dto.TipoTarjeta = vta[14].Trim();
                dto.EmvAid = vta[16].Trim();
                dto.Pan = vta[17].Trim();
            }
            else if (vta.Length >= 4)
            {
                dto.EmvAid = vta[^3].Trim();
                dto.Pan = vta[^2].Trim();
            }
            dto.NumControl = vta[^1].Trim();
            return dto;
        }

        // El wrapper reporta otros estados ademas de A/D/E (R=rechazada por
        // la terminal, BIN=lectura parcial). Cualquiera distinto de "A" se
        // devuelve como aprobada=false; solo "E" escala a excepcion.
        if (campos.Length < 6)
            throw new HardwareDisconnectedException(
                $"Respuesta inesperada del wrapper Banorte: '{bloques[0].Trim()}'");

        dto.Estado = estado;
        dto.CodigoRespuesta = estado == "A"
            ? "00"
            : (string.IsNullOrWhiteSpace(Campo(campos, 1)) ? estado : Campo(campos, 1));
        dto.MensajeRespuesta = Campo(campos, 2);
        dto.CodigoAutorizacion = Campo(campos, 3);
        dto.ReferenciaPayworks = Campo(campos, 4);
        dto.VoucherTexto = Campo(campos, 5);
        if (string.IsNullOrWhiteSpace(dto.MensajeRespuesta) && estado == "A")
            dto.MensajeRespuesta = "Aprobada";

        if (campos.Length >= 18)
        {
            dto.Bin = Campo(campos, 6);
            var enmascarada = dto.Bin + "**********";
            dto.TarjetaEnmascarada = enmascarada.Length > 16 ? enmascarada[..16] : enmascarada;
            dto.Pan = Campo(campos, 7);
            dto.ModoEntrada = Campo(campos, 8);
            dto.UsoPin = Campo(campos, 9);
            dto.TipoTarjeta = Campo(campos, 10);
            dto.MarcaTarjeta = Campo(campos, 11);
            dto.BancoEmisor = Campo(campos, 12);
            dto.FechaExpiracion = Campo(campos, 13);
            dto.EmvAid = Campo(campos, 14);
            dto.EmvTvr = Campo(campos, 15);
            dto.EmvPrefName = Campo(campos, 16);
            dto.EmvLabel = Campo(campos, 17);
        }

        dto.Aprobada = estado == "A";

        if (estado == "E")
        {
            var msg = dto.MensajeRespuesta.ToUpperInvariant();
            // EX_WORKER = excepcion interna del exe; -1001 = args malformados
            // (bug nuestro al invocar). Ambos son falla del wrapper -> 503.
            if (dto.CodigoRespuesta is "EX_WORKER" or "-1001" || EsFalloComunicacion(msg))
                throw new HardwareDisconnectedException($"Fallo del wrapper/terminal: {dto.MensajeRespuesta}");
            if (msg.Contains("TIMEOUT") || msg.Contains("TIEMPO"))
                throw new HardwareTimeoutException($"La terminal reporto timeout: {dto.MensajeRespuesta}");
        }

        return dto;
    }

    // "E" por cancelacion/declinacion del usuario es un resultado de negocio
    // (el controller lo devuelve como 400 con Aprobada=false); solo los textos
    // de conexion/wrapper escalan a 503.
    private static bool EsFalloComunicacion(string msgUpper) =>
        msgUpper.Contains("CONECT") || msgUpper.Contains("CONEXION") ||
        msgUpper.Contains("SOCKET") || msgUpper.Contains("COMUNIC") ||
        msgUpper.Contains("WRAPPER") || msgUpper.Contains("UNREACH") ||
        msgUpper.Contains("INALCANZABLE") || msgUpper.Contains("REFUS") ||
        msgUpper.Contains("NO RESPONDE") || msgUpper.Contains("SIN RESPUESTA") ||
        msgUpper.Contains("NO SE PUDO") || msgUpper.Contains("NETWORK") ||
        msgUpper.Contains("ERROR DE RED");

    private static string Campo(string[] campos, int i)
        => i < campos.Length ? campos[i].Trim() : string.Empty;
}
