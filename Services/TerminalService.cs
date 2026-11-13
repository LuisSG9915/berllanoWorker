using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using berllanoWorker.Exceptions;
using berllanoWorker.Interfaces;
using berllanoWorker.Models;
using Microsoft.Data.SqlClient;

namespace berllanoWorker.Services;

// Singleton: cada pinpad Banorte/Payworks es un recurso exclusivo por LAN
// (un socket TCP a la vez). Un Semaforo(1,1) POR IP serializa operaciones
// concurrentes hacia la misma terminal; el wrapper se invoca via
// BanorteWorker.exe (proceso) o BanortePayworksWrapper.dll (reflection).
// Credenciales/config por (sucursal,caja): IConfiguracionCajaService (cberp).
public class TerminalService : ITerminalService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locksPorTerminal =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TerminalService> _logger;
    private readonly IConfiguracionCajaService _configCaja;
    private readonly IImpresionService _impresionService;

    private readonly string _rutaWorkerExe;
    private readonly string _rutaWrapperDll;
    private readonly int _timeoutSegundos;
    private readonly bool _validarPingTcpPrevio;

    // Alias de la credencial resuelta (mantiene la firma interna corta).
    private sealed record ConfigEfectiva(TerminalCredencialesDto D)
    {
        public string Ip => D.IpTerminal;
        public int Puerto => D.Puerto;
        public string PuertoStr => D.PuertoStr;
        public string Usuario => D.Usuario;
        public string Password => D.Password;
        public string Modo => D.Modo;
        public string Afiliacion => D.Afiliacion;
        public int Sucursal => D.Sucursal;
        public int Caja => D.Caja;
        public string IdTerminal => D.IdTerminal;
    }

    public TerminalService(IConfiguration configuration, ILogger<TerminalService> logger,
        IConfiguracionCajaService configCaja, IImpresionService impresionService)
    {
        _logger = logger;
        _configCaja = configCaja;
        _impresionService = impresionService;
        _rutaWorkerExe = configuration.GetValue<string>("TerminalBanorte:RutaWorkerExe")
            ?? @"C:\asesor\BanortePOS\BanorteWorker.exe";
        _rutaWrapperDll = configuration.GetValue<string>("TerminalBanorte:RutaWrapperDll")
            ?? @"C:\asesor\BanortePOS\BanortePayworksWrapper.dll";
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

    public async Task<TerminalTransaccionResponseDto> InicializarLlavesAsync(TerminalLlavesRequestDto request, CancellationToken ct = default)
    {
        var cfg = await ResolverConfiguracionTerminalAsync(request, ct);
        var ahora = DateTime.Now;
        return await EjecutarAsync(cfg, "CARGAR_LLAVE",
            numControl: "KEY" + ahora.ToString("yyyyMMddHHmmss"),
            uuid: "UUID-KEY-" + ahora.ToString("yyyyMMddHHmmss"),
            monto: 0.00m, efectivo: 0.00m, ct);
    }

    public async Task<TerminalCobroResumenResponseDto> CobrarAsync(TerminalCobroRequestDto request, CancellationToken ct = default)
    {
        ValidarMonto(request.Monto);

        if (request.MontoRetiro < 0)
            throw new HardwareValidationException("El monto de retiro no puede ser negativo.");

        var cfg = await ResolverConfiguracionTerminalAsync(request, ct);
        var ahora = DateTime.Now;

        TerminalTransaccionResponseDto res;
        if (request.MontoRetiro > 0)
        {
            // CASHBACK: cobro con retiro de efectivo
            res = await EjecutarAsync(cfg, "CASHBACK",
                numControl: ahora.ToString("ddHHmmss") + "C",
                uuid: "UUID-" + ahora.ToString("yyyyMMddHHmmss") + "-C",
                monto: request.Monto, efectivo: request.MontoRetiro, ct);
        }
        else
        {
            // VENTA normal
            res = await EjecutarAsync(cfg, "VENTA",
                numControl: ahora.ToString("ddHHmmss") + "B",
                uuid: "UUID-" + ahora.ToString("yyyyMMddHHmmss"),
                monto: request.Monto, efectivo: 0.00m, ct);
        }

        res.ReferenciaCliente = request.Referencia;

        var resumen = new TerminalCobroResumenResponseDto
        {
            Aprobada = res.Aprobada,
            Estado = res.Estado,
            CodigoRespuesta = res.CodigoRespuesta,
            MensajeRespuesta = res.MensajeRespuesta
        };

        // Bloque 1: Inserción en BD (sp_registra_pago_banorte2026)
        if (request.NoVenta > 0)
        {
            try
            {
                var connString = _configCaja.ObtenerConnectionString();
                if (!string.IsNullOrWhiteSpace(connString))
                {
                    // Calcular parámetros según especificación
                    var tarjeta = "";
                    if (res.Pan.Length >= 4)
                    {
                        var bin = !string.IsNullOrEmpty(res.Bin) ? res.Bin : "******";
                        var base16 = (bin + "**********")[..Math.Min(12, (bin + "**********").Length)];
                        tarjeta = base16 + res.Pan[^4..];
                        if (tarjeta.Length > 16) tarjeta = tarjeta[..16];
                    }

                    var codigoTransaccion = res.NumControl;
                    var importe = (double)request.Monto;
                    var numAfiliacion = cfg.Afiliacion;
                    var numeroSesion = "0";
                    var codigoRespuesta = res.Aprobada ? "00" : res.CodigoRespuesta;
                    var aplicada = res.Aprobada;
                    var numAutorizacion = res.CodigoAutorizacion ?? "";
                    var mensajeRespuesta = res.Aprobada ? "Aprobado" : 
                        (string.IsNullOrWhiteSpace(res.MensajeRespuesta) ? "DECLINADA POR TERMINAL/BANCO" : res.MensajeRespuesta);
                    var tipoTarjeta = (!string.IsNullOrWhiteSpace(res.EmvPrefName) ? res.EmvPrefName : res.TipoTarjeta);
                    if (tipoTarjeta.Length > 20) tipoTarjeta = tipoTarjeta[..20];
                    var nombreCliente = "";
                    var idEncriptacion = "";
                    var cancelada = false;
                    var refFinanciera = string.IsNullOrWhiteSpace(res.ReferenciaPayworks) ? "00000000" : res.ReferenciaPayworks;
                    var montoRetiro = request.MontoRetiro > 0 ? ((int)Math.Round(request.MontoRetiro)).ToString() : "0";
                    var usuario = request.UsuarioPos;
                    var corte = request.Corte;
                    var corteParcial = request.CorteParcial;
                    var efectivo = (double)request.Efectivo;
                    var pagoCombinado = request.PagoCombinado ?? "";

                    const string sql = """
                        EXEC dbo.sp_registra_pago_banorte2026
                            @sucursal, @no_venta, @caja, @tipo_pago, @tarjeta, @codigo_transaccion,
                            @importe, @num_afiliacion, @numero_sesion, @codigo_respuesta, @aplicada,
                            @num_autorizacion, @mensaje_respuesta, @tipo_tarjeta, @nombre_cliente,
                            @id_encriptacion, @cancelada, @ref_financiera, @monto_retiro,
                            @usuario, @corte, @corte_parcial, @efectivo, @pago_combinado
                        """;

                    await using var cn = new SqlConnection(connString);
                    await cn.OpenAsync(ct);
                    await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 30 };
                    cmd.Parameters.AddWithValue("@sucursal", (short)cfg.Sucursal);
                    cmd.Parameters.AddWithValue("@no_venta", request.NoVenta);
                    cmd.Parameters.AddWithValue("@caja", (short)cfg.Caja);
                    cmd.Parameters.AddWithValue("@tipo_pago", 7);
                    cmd.Parameters.AddWithValue("@tarjeta", tarjeta);
                    cmd.Parameters.AddWithValue("@codigo_transaccion", codigoTransaccion);
                    cmd.Parameters.AddWithValue("@importe", importe);
                    cmd.Parameters.AddWithValue("@num_afiliacion", numAfiliacion);
                    cmd.Parameters.AddWithValue("@numero_sesion", numeroSesion);
                    cmd.Parameters.AddWithValue("@codigo_respuesta", codigoRespuesta);
                    cmd.Parameters.AddWithValue("@aplicada", aplicada);
                    cmd.Parameters.AddWithValue("@num_autorizacion", numAutorizacion);
                    cmd.Parameters.AddWithValue("@mensaje_respuesta", mensajeRespuesta);
                    cmd.Parameters.AddWithValue("@tipo_tarjeta", tipoTarjeta);
                    cmd.Parameters.AddWithValue("@nombre_cliente", nombreCliente);
                    cmd.Parameters.AddWithValue("@id_encriptacion", idEncriptacion);
                    cmd.Parameters.AddWithValue("@cancelada", cancelada);
                    cmd.Parameters.AddWithValue("@ref_financiera", refFinanciera);
                    cmd.Parameters.AddWithValue("@monto_retiro", montoRetiro);
                    cmd.Parameters.AddWithValue("@usuario", usuario);
                    cmd.Parameters.AddWithValue("@corte", corte);
                    cmd.Parameters.AddWithValue("@corte_parcial", corteParcial);
                    cmd.Parameters.AddWithValue("@efectivo", efectivo);
                    cmd.Parameters.AddWithValue("@pago_combinado", pagoCombinado);

                    await cmd.ExecuteNonQueryAsync(ct);

                    // Obtener numero_secuencia insertado
                    const string sqlSeq = """
                        SELECT TOP 1 numero_secuencia
                        FROM dbo.detalle_ventas_pagos WITH (NOLOCK)
                        WHERE sucursal = @sucursal AND caja = @caja AND no_venta = @no_venta AND codigo_transaccion = @codigo_transaccion
                        ORDER BY numero_secuencia DESC;
                        """;

                    await using var cmdSeq = new SqlCommand(sqlSeq, cn) { CommandTimeout = 10 };
                    cmdSeq.Parameters.AddWithValue("@sucursal", (short)cfg.Sucursal);
                    cmdSeq.Parameters.AddWithValue("@caja", (short)cfg.Caja);
                    cmdSeq.Parameters.AddWithValue("@no_venta", request.NoVenta);
                    cmdSeq.Parameters.AddWithValue("@codigo_transaccion", codigoTransaccion);

                    var seqResult = await cmdSeq.ExecuteScalarAsync(ct);
                    if (seqResult != null && seqResult != DBNull.Value)
                    {
                        res.NumeroSecuencia = Convert.ToInt64(seqResult);
                        resumen.VentaInsertada = true;
                        resumen.MensajeInsercion = $"Pago registrado en cberp (Secuencia: {res.NumeroSecuencia})";
                    }
                    else
                    {
                        resumen.VentaInsertada = false;
                        resumen.MensajeInsercion = "No se pudo obtener numero_secuencia";
                    }
                }
                else
                {
                    resumen.VentaInsertada = false;
                    resumen.MensajeInsercion = "Connection string no configurado";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error registrando pago en sp_registra_pago_banorte2026: {Msg}", ex.Message);
                resumen.VentaInsertada = false;
                resumen.MensajeInsercion = $"Error SQL: {ex.Message}";
            }
        }
        else
        {
            resumen.VentaInsertada = false;
            resumen.MensajeInsercion = "Omitido (no_venta no especificado)";
        }

        // Bloque 2: Impresión del Voucher (sp_ticket_banorte)
        if (request.ImprimirVoucher && request.NoVenta > 0 && resumen.VentaInsertada)
        {
            try
            {
                var connString = _configCaja.ObtenerConnectionString();
                if (!string.IsNullOrWhiteSpace(connString))
                {
                    // Preparar parámetros para sp_ticket_banorte
                    var folio = (int)request.NoVenta;
                    var caja = cfg.Caja;
                    var suc = cfg.Sucursal;
                    var autorizacion = res.CodigoAutorizacion;
                    var secuencia = res.NumeroSecuencia ?? 0;
                    var referenciaTransaccion = res.ReferenciaPayworks;
                    var tipoPago = 7;
                    var numTerminal = cfg.Afiliacion;
                    var total = (double)request.Monto;
                    var expiracionTarjeta = res.FechaExpiracion.Length == 4 ? 
                        res.FechaExpiracion[..2] + "/" + res.FechaExpiracion[2..] : res.FechaExpiracion;
                    var tipoTarjetaV = !string.IsNullOrWhiteSpace(res.MarcaTarjeta) ? res.MarcaTarjeta : "VISA";
                    var bancoPlastico = !string.IsNullOrWhiteSpace(res.BancoEmisor) ? res.BancoEmisor : "BANORTE";
                    var american = res.ModoEntrada;
                    var tipoTrans = request.MontoRetiro > 0 ? "Venta con Cashback" : "Venta";
                    var numControl = res.NumControl;
                    var estadoAutorizacion = res.Aprobada ? "APROBADA" : "DECLINADA";
                    var referenciaOriginal = "";
                    var tipoTarj = (res.EmvPrefName.Contains("DEB", StringComparison.OrdinalIgnoreCase) || 
                                   res.TipoTarjeta.Contains("DEB", StringComparison.OrdinalIgnoreCase)) ? "D" : "C";
                    var pin = res.UsoPin == "1";
                    var fechaTran = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
                    var tarjetaReferida = res.Pan.Length >= 2 ? res.Pan[^2..] : "";

                    // Construir tags_EMV
                    var aidPadded = (res.EmvAid ?? "").PadRight(14, ' ');
                    var tvrPadded = (res.EmvTvr ?? "").PadRight(10, ' ');
                    var tagsEmv = $"4F07{aidPadded}9505{tvrPadded}";

                    const string sql = """
                        EXEC dbo.sp_ticket_banorte
                            @folio, @caja, @suc, @autorizacion, @secuencia, @referencia_transaccion,
                            @tipo_pago, @num_terminal, @total, @expiracion_tarjeta, @tipo_tarjeta_v,
                            @banco_plastico, @american, @tipo_trans, @num_control, @estado_autorizacion,
                            @referencia_original, @tipo_tarj, @pin, @fecha_tran, @tarjeta_referida, @tags_EMV
                        """;

                    var lineas = new List<VoucherLineaDto>();
                    await using var cn = new SqlConnection(connString);
                    await cn.OpenAsync(ct);
                    await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 30 };
                    cmd.Parameters.AddWithValue("@folio", folio);
                    cmd.Parameters.AddWithValue("@caja", caja);
                    cmd.Parameters.AddWithValue("@suc", suc);
                    cmd.Parameters.AddWithValue("@autorizacion", autorizacion);
                    cmd.Parameters.AddWithValue("@secuencia", secuencia);
                    cmd.Parameters.AddWithValue("@referencia_transaccion", referenciaTransaccion);
                    cmd.Parameters.AddWithValue("@tipo_pago", tipoPago);
                    cmd.Parameters.AddWithValue("@num_terminal", numTerminal);
                    cmd.Parameters.AddWithValue("@total", total);
                    cmd.Parameters.AddWithValue("@expiracion_tarjeta", expiracionTarjeta);
                    cmd.Parameters.AddWithValue("@tipo_tarjeta_v", tipoTarjetaV);
                    cmd.Parameters.AddWithValue("@banco_plastico", bancoPlastico);
                    cmd.Parameters.AddWithValue("@american", american);
                    cmd.Parameters.AddWithValue("@tipo_trans", tipoTrans);
                    cmd.Parameters.AddWithValue("@num_control", numControl);
                    cmd.Parameters.AddWithValue("@estado_autorizacion", estadoAutorizacion);
                    cmd.Parameters.AddWithValue("@referencia_original", referenciaOriginal);
                    cmd.Parameters.AddWithValue("@tipo_tarj", tipoTarj);
                    cmd.Parameters.AddWithValue("@pin", pin);
                    cmd.Parameters.AddWithValue("@fecha_tran", fechaTran);
                    cmd.Parameters.AddWithValue("@tarjeta_referida", tarjetaReferida);
                    cmd.Parameters.AddWithValue("@tags_EMV", tagsEmv);

                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                    {
                        var cons = rd.GetDouble(0);
                        var tipo = rd.GetString(1);
                        var linea = rd.GetString(2);
                        lineas.Add(new VoucherLineaDto { Cons = cons, Tipo = tipo, Linea = linea });
                    }

                    // Ordenar por CONS ASC
                    lineas = lineas.OrderBy(l => l.Cons).ToList();
                    res.VoucherLineas = lineas;

                    // Imprimir voucher
                    var ticketLineas = new List<TicketLineaDto>();
                    foreach (var linea in lineas)
                    {
                        var consecutivo = (decimal)linea.Cons;
                        if (linea.Cons == 99)
                        {
                            // Corte de papel
                            ticketLineas.Add(new TicketLineaDto { Consecutivo = consecutivo, Modo = "CORTE_PAPEL", Data = "" });
                        }
                        else if (linea.Cons == 100 && !string.IsNullOrEmpty(linea.Linea))
                        {
                            // Pulso de cajón
                            ticketLineas.Add(new TicketLineaDto { Consecutivo = consecutivo, Modo = "ABRIR_CAJON", Data = "" });
                        }
                        else if (linea.Cons < 99)
                        {
                            // Texto plano
                            ticketLineas.Add(new TicketLineaDto { Consecutivo = consecutivo, Modo = "TEXTO_PLANO", Data = linea.Linea });
                        }
                    }

                    var ticketRequest = new TicketRequestDto
                    {
                        Recurso = "Tickets",
                        Sucursal = cfg.Sucursal,
                        Caja = cfg.Caja,
                        TicketDataset = ticketLineas
                    };

                    await _impresionService.ImprimirTicketAsync(ticketRequest);
                    resumen.TicketImpreso = true;
                    resumen.MensajeTicket = "Voucher enviado a impresora";
                }
                else
                {
                    resumen.TicketImpreso = false;
                    resumen.MensajeTicket = "Connection string no configurado";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error generando/imprimiendo voucher: {Msg}", ex.Message);
                resumen.TicketImpreso = false;
                resumen.MensajeTicket = $"Error al imprimir voucher: {ex.Message}";
            }
        }
        else
        {
            resumen.TicketImpreso = false;
            resumen.MensajeTicket = "Omitido (no_venta no especificado o venta no insertada)";
        }

        return resumen;
    }

    public async Task<TerminalTransaccionResponseDto> RetirarEfectivoAsync(TerminalRetiroRequestDto request, CancellationToken ct = default)
    {
        ValidarMonto(request.Monto);

        var cfg = await ResolverConfiguracionTerminalAsync(request, ct);
        var ahora = DateTime.Now;
        return await EjecutarAsync(cfg, "CASHBACK",
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

    public async Task<TerminalTransaccionResponseDto> CancelarTransaccionAsync(TerminalCancelacionRequestDto request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.TransaccionId))
            throw new HardwareValidationException("El campo 'transaccion_id' es obligatorio para cancelar.");

        var cfg = await ResolverConfiguracionTerminalAsync(request, ct);
        var id = request.TransaccionId.Trim();

        // Devolucion financiera real: solo si el id es una Referencia
        // Payworks (numerica de 12 digitos) y llega el monto a reversar.
        if (EsReferenciaPayworks(id) && request.Monto.GetValueOrDefault() > 0)
        {
            ValidarMonto(request.Monto!.Value);
            var dto = await ConBloqueoAsync(cfg,
                token => EjecutarDevolucionWrapperAsync(cfg, id, request.Monto.Value, token), ct);

            // Reversas en BD si está aprobado y hay no_venta
            if (dto.Aprobada && request.NoVenta.HasValue && request.NoVenta.Value > 0)
            {
                await EjecutarReversasBDAsync(request, cfg, id, ct);
            }

            return dto;
        }

        // Caso general: reset de sesion EMV (CANCELAR / PPSendTxn(3)).
        return await EjecutarAsync(cfg, "CANCELAR",
            numControl: id,
            uuid: "UUID-CAN-" + DateTime.Now.ToString("yyyyMMddHHmmss"),
            monto: 0.00m, efectivo: 0.00m, ct);
    }

    // Ejecutar reversas en BD después de cancelación/devolución exitosa
    private async Task EjecutarReversasBDAsync(TerminalCancelacionRequestDto request, ConfigEfectiva cfg, string referenciaPayworks, CancellationToken ct)
    {
        try
        {
            var connString = _configCaja.ObtenerConnectionString();
            if (string.IsNullOrWhiteSpace(connString)) return;

            await using var cn = new SqlConnection(connString);
            await cn.OpenAsync(ct);

            // 1. Marcar pago como cancelado en detalle_ventas_pagos
            const string sql1 = """
                UPDATE dbo.detalle_ventas_pagos
                SET aplicada = 0, cancelada = 1, leyenda_respuesta = 'Cancelada'
                WHERE ref_financiera = @ref AND sucursal = @sucursal AND caja = @caja AND no_venta = @no_venta;
                """;

            await using (var cmd1 = new SqlCommand(sql1, cn) { CommandTimeout = 30 })
            {
                cmd1.Parameters.AddWithValue("@ref", referenciaPayworks);
                cmd1.Parameters.AddWithValue("@sucursal", (short)cfg.Sucursal);
                cmd1.Parameters.AddWithValue("@caja", (short)cfg.Caja);
                cmd1.Parameters.AddWithValue("@no_venta", request.NoVenta!.Value);
                await cmd1.ExecuteNonQueryAsync(ct);
            }

            // 2. Si reversa_venta == true, reversar la venta completa
            if (request.ReversaVenta)
            {
                const string sql2 = """
                    UPDATE dbo.detalle_ventas SET observacion = 'Cancelacion TC', cant_producto = 0
                    WHERE sucursal = @sucursal AND caja = @caja AND no_venta = @no_venta;

                    UPDATE dbo.detalle_ventas_medios_pagos SET referencia = referencia + '-Cancelacion TC', importe = 0
                    WHERE sucursal = @sucursal AND caja = @caja AND folio_venta = @no_venta;
                    """;

                await using var cmd2 = new SqlCommand(sql2, cn) { CommandTimeout = 30 };
                cmd2.Parameters.AddWithValue("@sucursal", (short)cfg.Sucursal);
                cmd2.Parameters.AddWithValue("@caja", (short)cfg.Caja);
                cmd2.Parameters.AddWithValue("@no_venta", request.NoVenta!.Value);
                await cmd2.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error ejecutando reversas en BD: {Msg}", ex.Message);
            // No lanzar excepción para no afectar la cancelación principal
        }
    }

    private static bool EsReferenciaPayworks(string id)
        => id.Length == 12 && id.All(char.IsDigit);

    public async Task<TerminalTransaccionResponseDto> VerificarTransaccionAsync(TerminalVerificarRequestDto request, CancellationToken ct = default)
    {
        // Equivalente a la validacion numControlOriginal = "" del VBA
        if (string.IsNullOrWhiteSpace(request.NumeroControl))
            throw new HardwareValidationException("El campo 'numero_control' es obligatorio para verificar.");

        var cfg = await ResolverConfiguracionTerminalAsync(request, ct);
        return await EjecutarAsync(cfg, "VERIFICACION",
            numControl: request.NumeroControl.Trim(),
            uuid: "UUID-VER-" + DateTime.Now.ToString("yyyyMMddHHmmss"),
            monto: 0.00m, efectivo: 0.00m, ct);
    }

    // Resuelve credenciales/destino de la terminal via el servicio central
    // multi-caja (t_banorte_usuarios + recurso 'TerminalBanorte' en
    // cat_RecursosWorker + overrides del request + fallback local de esta PC).
    private async Task<ConfigEfectiva> ResolverConfiguracionTerminalAsync(
        TerminalConfigOverrideDto request, CancellationToken ct)
        => new(await _configCaja.ResolverTerminalCajaAsync(request, ct));

    // GET /api/terminal/cajas: delega al servicio central (sin pass_banorte).
    public Task<List<TerminalCajaDto>> ObtenerCajasAsync(int? sucursal, CancellationToken ct = default)
        => _configCaja.ObtenerCajasTerminalAsync(sucursal, ct);

    // GET /api/terminal/terminales: lista de terminales con estado online
    public Task<List<TerminalInfoDto>> ObtenerTerminalesAsync(int? sucursal, bool verificarPing, CancellationToken ct = default)
        => _configCaja.ObtenerTerminalesConEstadoAsync(sucursal, verificarPing, ct);

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

    // Semaforo exclusivo POR pinpad (clave: IP) + chequeo TCP previo para
    // cualquier operacion que hable con la terminal (exe o wrapper in-proc).
    private async Task<TerminalTransaccionResponseDto> ConBloqueoAsync(
        ConfigEfectiva cfg, Func<CancellationToken, Task<TerminalTransaccionResponseDto>> accion, CancellationToken ct)
    {
        var pinpadLock = _locksPorTerminal.GetOrAdd(cfg.Ip, _ => new SemaphoreSlim(1, 1));

        // Rechazo inmediato con 423 si ESTA terminal ya esta en uso (otras
        // IPs pueden operar en paralelo en un equipo multi-caja).
        if (!await pinpadLock.WaitAsync(0, ct))
            throw new HardwareBusyException(
                $"La terminal de la Caja {cfg.Caja} ({cfg.Ip}) esta ocupada procesando otra operacion. " +
                "Reintenta en unos segundos.");

        try
        {
            if (_validarPingTcpPrevio)
                await ValidarConexionTcpAsync(cfg.Ip, cfg.Puerto, ct);

            var dto = await accion(ct);

            // Eco de la resolucion para que el cliente confirme la caja real.
            dto.Sucursal = cfg.Sucursal;
            dto.Caja = cfg.Caja;
            dto.IpTerminal = cfg.Ip;
            dto.IdTerminal = cfg.IdTerminal;
            dto.Afiliacion = cfg.Afiliacion;
            return dto;
        }
        finally
        {
            // Pausa deliberada antes de liberar: da tiempo a que el socket TCP
            // de la terminal quede libre para la siguiente operacion.
            await Task.Delay(500);
            pinpadLock.Release();
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
            string mensajeDetalle = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused =>
                    "La terminal está encendida pero no está en la aplicación del banco. " +
                    "Por favor, abre la aplicación Banorte en la terminal y vuelve a intentar.",
                SocketError.TimedOut =>
                    "La terminal no responde. Verifica que esté encendida y en la misma red.",
                SocketError.HostUnreachable =>
                    "No se puede alcanzar la terminal. Verifica que esté encendida y en la misma red.",
                SocketError.NetworkUnreachable =>
                    "No hay conexión de red hacia la terminal. Verifica la configuración de red.",
                _ =>
                    $"Error de conexión con la terminal: {ex.SocketErrorCode}. " +
                    "Verifica que esté encendida y en la misma red."
            };

            throw new HardwareDisconnectedException(
                $"Sin conexion TCP con la terminal {ip}:{puerto}: {mensajeDetalle}");
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

    // FASE 2: Registrar pago en sp_registra_pago_banorte2026
    private async Task RegistrarPagoBanorteAsync(TerminalTransaccionResponseDto res, TerminalCobroRequestDto request, ConfigEfectiva cfg, CancellationToken ct)
    {
        try
        {
            var connString = _configCaja.ObtenerConnectionString();
            if (string.IsNullOrWhiteSpace(connString)) return;

            // Calcular parámetros según especificación
            var tarjeta = "";
            if (res.Pan.Length >= 4)
            {
                var bin = !string.IsNullOrEmpty(res.Bin) ? res.Bin : "******";
                var base16 = (bin + "**********")[..Math.Min(12, (bin + "**********").Length)];
                tarjeta = base16 + res.Pan[^4..];
                if (tarjeta.Length > 16) tarjeta = tarjeta[..16];
            }

            var codigoTransaccion = res.NumControl;
            var importe = (double)request.Monto;
            var numAfiliacion = cfg.Afiliacion;
            var numeroSesion = "0";
            var codigoRespuesta = res.Aprobada ? "00" : res.CodigoRespuesta;
            var aplicada = res.Aprobada;
            var numAutorizacion = res.CodigoAutorizacion ?? "";
            var mensajeRespuesta = res.Aprobada ? "Aprobado" : 
                (string.IsNullOrWhiteSpace(res.MensajeRespuesta) ? "DECLINADA POR TERMINAL/BANCO" : res.MensajeRespuesta);
            var tipoTarjeta = (!string.IsNullOrWhiteSpace(res.EmvPrefName) ? res.EmvPrefName : res.TipoTarjeta);
            if (tipoTarjeta.Length > 20) tipoTarjeta = tipoTarjeta[..20];
            var nombreCliente = "";
            var idEncriptacion = "";
            var cancelada = false;
            var refFinanciera = string.IsNullOrWhiteSpace(res.ReferenciaPayworks) ? "00000000" : res.ReferenciaPayworks;
            var montoRetiro = request.MontoRetiro > 0 ? ((int)Math.Round(request.MontoRetiro)).ToString() : "0";
            var usuario = request.UsuarioPos;
            var corte = request.Corte;
            var corteParcial = request.CorteParcial;
            var efectivo = (double)request.Efectivo;
            var pagoCombinado = request.PagoCombinado ?? "";

            const string sql = """
                EXEC dbo.sp_registra_pago_banorte2026
                    @sucursal, @no_venta, @caja, @tipo_pago, @tarjeta, @codigo_transaccion,
                    @importe, @num_afiliacion, @numero_sesion, @codigo_respuesta, @aplicada,
                    @num_autorizacion, @mensaje_respuesta, @tipo_tarjeta, @nombre_cliente,
                    @id_encriptacion, @cancelada, @ref_financiera, @monto_retiro,
                    @usuario, @corte, @corte_parcial, @efectivo, @pago_combinado
                """;

            await using var cn = new SqlConnection(connString);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@sucursal", (short)cfg.Sucursal);
            cmd.Parameters.AddWithValue("@no_venta", request.NoVenta);
            cmd.Parameters.AddWithValue("@caja", (short)cfg.Caja);
            cmd.Parameters.AddWithValue("@tipo_pago", 7);
            cmd.Parameters.AddWithValue("@tarjeta", tarjeta);
            cmd.Parameters.AddWithValue("@codigo_transaccion", codigoTransaccion);
            cmd.Parameters.AddWithValue("@importe", importe);
            cmd.Parameters.AddWithValue("@num_afiliacion", numAfiliacion);
            cmd.Parameters.AddWithValue("@numero_sesion", numeroSesion);
            cmd.Parameters.AddWithValue("@codigo_respuesta", codigoRespuesta);
            cmd.Parameters.AddWithValue("@aplicada", aplicada);
            cmd.Parameters.AddWithValue("@num_autorizacion", numAutorizacion);
            cmd.Parameters.AddWithValue("@mensaje_respuesta", mensajeRespuesta);
            cmd.Parameters.AddWithValue("@tipo_tarjeta", tipoTarjeta);
            cmd.Parameters.AddWithValue("@nombre_cliente", nombreCliente);
            cmd.Parameters.AddWithValue("@id_encriptacion", idEncriptacion);
            cmd.Parameters.AddWithValue("@cancelada", cancelada);
            cmd.Parameters.AddWithValue("@ref_financiera", refFinanciera);
            cmd.Parameters.AddWithValue("@monto_retiro", montoRetiro);
            cmd.Parameters.AddWithValue("@usuario", usuario);
            cmd.Parameters.AddWithValue("@corte", corte);
            cmd.Parameters.AddWithValue("@corte_parcial", corteParcial);
            cmd.Parameters.AddWithValue("@efectivo", efectivo);
            cmd.Parameters.AddWithValue("@pago_combinado", pagoCombinado);

            await cmd.ExecuteNonQueryAsync(ct);

            // Obtener numero_secuencia insertado
            const string sqlSeq = """
                SELECT TOP 1 numero_secuencia
                FROM dbo.detalle_ventas_pagos WITH (NOLOCK)
                WHERE sucursal = @sucursal AND caja = @caja AND no_venta = @no_venta AND codigo_transaccion = @codigo_transaccion
                ORDER BY numero_secuencia DESC;
                """;

            await using var cmdSeq = new SqlCommand(sqlSeq, cn) { CommandTimeout = 10 };
            cmdSeq.Parameters.AddWithValue("@sucursal", (short)cfg.Sucursal);
            cmdSeq.Parameters.AddWithValue("@caja", (short)cfg.Caja);
            cmdSeq.Parameters.AddWithValue("@no_venta", request.NoVenta);
            cmdSeq.Parameters.AddWithValue("@codigo_transaccion", codigoTransaccion);

            var seqResult = await cmdSeq.ExecuteScalarAsync(ct);
            if (seqResult != null && seqResult != DBNull.Value)
            {
                res.NumeroSecuencia = Convert.ToInt64(seqResult);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error registrando pago en sp_registra_pago_banorte2026: {Msg}", ex.Message);
            // No lanzar excepción para no afectar la transacción principal
        }
    }

    // FASE 3: Generar e imprimir voucher usando sp_ticket_banorte
    private async Task GenerarEImprimirVoucherAsync(TerminalTransaccionResponseDto res, TerminalCobroRequestDto request, ConfigEfectiva cfg, CancellationToken ct)
    {
        try
        {
            var connString = _configCaja.ObtenerConnectionString();
            if (string.IsNullOrWhiteSpace(connString)) return;

            // Preparar parámetros para sp_ticket_banorte
            var folio = (int)request.NoVenta;
            var caja = cfg.Caja;
            var suc = cfg.Sucursal;
            var autorizacion = res.CodigoAutorizacion;
            var secuencia = res.NumeroSecuencia ?? 0;
            var referenciaTransaccion = res.ReferenciaPayworks;
            var tipoPago = 7;
            var numTerminal = cfg.Afiliacion;
            var total = (double)request.Monto;
            var expiracionTarjeta = res.FechaExpiracion.Length == 4 ? 
                res.FechaExpiracion[..2] + "/" + res.FechaExpiracion[2..] : res.FechaExpiracion;
            var tipoTarjetaV = !string.IsNullOrWhiteSpace(res.MarcaTarjeta) ? res.MarcaTarjeta : "VISA";
            var bancoPlastico = !string.IsNullOrWhiteSpace(res.BancoEmisor) ? res.BancoEmisor : "BANORTE";
            var american = res.ModoEntrada;
            var tipoTrans = request.MontoRetiro > 0 ? "Venta con Cashback" : "Venta";
            var numControl = res.NumControl;
            var estadoAutorizacion = res.Aprobada ? "APROBADA" : "DECLINADA";
            var referenciaOriginal = "";
            var tipoTarj = (res.EmvPrefName.Contains("DEB", StringComparison.OrdinalIgnoreCase) || 
                           res.TipoTarjeta.Contains("DEB", StringComparison.OrdinalIgnoreCase)) ? "D" : "C";
            var pin = res.UsoPin == "1";
            var fechaTran = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
            var tarjetaReferida = res.Pan.Length >= 2 ? res.Pan[^2..] : "";

            // Construir tags_EMV
            var aidPadded = (res.EmvAid ?? "").PadRight(14, ' ');
            var tvrPadded = (res.EmvTvr ?? "").PadRight(10, ' ');
            var tagsEmv = $"4F07{aidPadded}9505{tvrPadded}";

            const string sql = """
                EXEC dbo.sp_ticket_banorte
                    @folio, @caja, @suc, @autorizacion, @secuencia, @referencia_transaccion,
                    @tipo_pago, @num_terminal, @total, @expiracion_tarjeta, @tipo_tarjeta_v,
                    @banco_plastico, @american, @tipo_trans, @num_control, @estado_autorizacion,
                    @referencia_original, @tipo_tarj, @pin, @fecha_tran, @tarjeta_referida, @tags_EMV
                """;

            var lineas = new List<VoucherLineaDto>();
            await using var cn = new SqlConnection(connString);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@folio", folio);
            cmd.Parameters.AddWithValue("@caja", caja);
            cmd.Parameters.AddWithValue("@suc", suc);
            cmd.Parameters.AddWithValue("@autorizacion", autorizacion);
            cmd.Parameters.AddWithValue("@secuencia", secuencia);
            cmd.Parameters.AddWithValue("@referencia_transaccion", referenciaTransaccion);
            cmd.Parameters.AddWithValue("@tipo_pago", tipoPago);
            cmd.Parameters.AddWithValue("@num_terminal", numTerminal);
            cmd.Parameters.AddWithValue("@total", total);
            cmd.Parameters.AddWithValue("@expiracion_tarjeta", expiracionTarjeta);
            cmd.Parameters.AddWithValue("@tipo_tarjeta_v", tipoTarjetaV);
            cmd.Parameters.AddWithValue("@banco_plastico", bancoPlastico);
            cmd.Parameters.AddWithValue("@american", american);
            cmd.Parameters.AddWithValue("@tipo_trans", tipoTrans);
            cmd.Parameters.AddWithValue("@num_control", numControl);
            cmd.Parameters.AddWithValue("@estado_autorizacion", estadoAutorizacion);
            cmd.Parameters.AddWithValue("@referencia_original", referenciaOriginal);
            cmd.Parameters.AddWithValue("@tipo_tarj", tipoTarj);
            cmd.Parameters.AddWithValue("@pin", pin);
            cmd.Parameters.AddWithValue("@fecha_tran", fechaTran);
            cmd.Parameters.AddWithValue("@tarjeta_referida", tarjetaReferida);
            cmd.Parameters.AddWithValue("@tags_EMV", tagsEmv);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var cons = rd.GetDouble(0);
                var tipo = rd.GetString(1);
                var linea = rd.GetString(2);
                lineas.Add(new VoucherLineaDto { Cons = cons, Tipo = tipo, Linea = linea });
            }

            // Ordenar por CONS ASC
            lineas = lineas.OrderBy(l => l.Cons).ToList();
            res.VoucherLineas = lineas;

            // Imprimir voucher si está habilitado
            if (request.ImprimirVoucher && res.Aprobada)
            {
                await ImprimirVoucherAsync(lineas, cfg, ct);
                res.VoucherImpreso = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error generando/imprimiendo voucher: {Msg}", ex.Message);
            res.ErrorImpresion = ex.Message;
            res.VoucherImpreso = false;
        }
    }

    // Imprimir voucher usando IImpresionService
    private async Task ImprimirVoucherAsync(List<VoucherLineaDto> lineas, ConfigEfectiva cfg, CancellationToken ct)
    {
        try
        {
            var ticketLineas = new List<TicketLineaDto>();
            foreach (var linea in lineas)
            {
                var consecutivo = (decimal)linea.Cons;
                if (linea.Cons == 99)
                {
                    // Corte de papel
                    ticketLineas.Add(new TicketLineaDto { Consecutivo = consecutivo, Modo = "CORTE_PAPEL", Data = "" });
                }
                else if (linea.Cons == 100 && !string.IsNullOrEmpty(linea.Linea))
                {
                    // Pulso de cajón - se maneja como modo especial
                    ticketLineas.Add(new TicketLineaDto { Consecutivo = consecutivo, Modo = "ABRIR_CAJON", Data = linea.Linea });
                }
                else if (linea.Cons < 99)
                {
                    // Texto plano
                    ticketLineas.Add(new TicketLineaDto { Consecutivo = consecutivo, Modo = "TEXTO_PLANO", Data = linea.Linea });
                }
            }

            var ticketRequest = new TicketRequestDto
            {
                Recurso = "Tickets",
                Sucursal = cfg.Sucursal,
                Caja = cfg.Caja,
                TicketDataset = ticketLineas
            };

            await _impresionService.ImprimirTicketAsync(ticketRequest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error imprimiendo voucher: {Msg}", ex.Message);
            throw;
        }
    }
}
