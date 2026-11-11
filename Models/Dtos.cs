using System.Text.Json.Serialization;

namespace berllanoWorker.Models;

// Contrato del docx: { "recurso": "Miniprinter_Caja1", "ticket_dataset": [ { consecutivo, modo, data } ] }
public class TicketLineaDto
{
    // decimal (no int) para aceptar consecutivos fraccionarios tipo "0.5", "13.543"
    // que usa el formato de tickets heredado; el ordenamiento sigue siendo numerico.
    [JsonPropertyName("consecutivo")]
    public decimal Consecutivo { get; set; }

    [JsonPropertyName("modo")]
    public string Modo { get; set; } = "TEXTO_PLANO";

    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;
}

public class TicketRequestDto
{
    // Nombre fisico de impresora Windows O alias de recurso en
    // cat_RecursosWorker ("Tickets", "Documentos", custom). Vacio -> "Tickets".
    [JsonPropertyName("recurso")]
    public string Recurso { get; set; } = string.Empty;

    [JsonPropertyName("sucursal")]
    public int? Sucursal { get; set; }

    [JsonPropertyName("caja")]
    public int? Caja { get; set; }

    [JsonPropertyName("forzar_recarga")]
    public bool ForzarRecarga { get; set; } = false;

    [JsonPropertyName("ticket_dataset")]
    public List<TicketLineaDto> TicketDataset { get; set; } = new();
}

// Contrato del docx: { "url_file": "https://empresa.com/doc.pdf", "recurso": "HP_LaserJet_Oficina" }
public class DocumentoRequestDto
{
    [JsonPropertyName("url_file")]
    public string UrlFile { get; set; } = string.Empty;

    // Igual que ticket: nombre fisico o alias. Vacio -> "Documentos".
    [JsonPropertyName("recurso")]
    public string Recurso { get; set; } = string.Empty;

    [JsonPropertyName("sucursal")]
    public int? Sucursal { get; set; }

    [JsonPropertyName("caja")]
    public int? Caja { get; set; }

    [JsonPropertyName("forzar_recarga")]
    public bool ForzarRecarga { get; set; } = false;
}

// Respuesta de GET /api/sistema/identidad
public class IdentidadDto
{
    public string Hostname { get; set; } = string.Empty;
    public string HostMdns { get; set; } = string.Empty;
    public List<string> IpsLan { get; set; } = new();
    public int Puerto { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Status { get; set; } = "ONLINE";
}

// Overrides opcionales por peticion. sucursal/caja seleccionan la fila de
// cberp.dbo.t_banorte_usuarios; los campos manuales ganan sobre la fila.
public class TerminalConfigOverrideDto
{
    [JsonPropertyName("sucursal")]
    public int? Sucursal { get; set; }

    [JsonPropertyName("caja")]
    public int? Caja { get; set; }

    [JsonPropertyName("forzar_recarga")]
    public bool ForzarRecarga { get; set; } = false;

    public string? IpTerminal { get; set; }
    public string? Puerto { get; set; }
    public string? Usuario { get; set; }
    public string? Password { get; set; }
    public string? Modo { get; set; }
    public string? Afiliacion { get; set; }
}

// POST /api/terminal/llaves -> {}
public class TerminalLlavesRequestDto : TerminalConfigOverrideDto { }

// POST /api/terminal/cobro -> { "monto": 1500.50, "referencia": "REF-VTA99231" }
public class TerminalCobroRequestDto : TerminalConfigOverrideDto
{
    [JsonPropertyName("monto")]
    public decimal Monto { get; set; }

    [JsonPropertyName("referencia")]
    public string Referencia { get; set; } = string.Empty;
}

// POST /api/terminal/retiro -> { "monto": 500.00, "monto_venta": 0 }
public class TerminalRetiroRequestDto : TerminalConfigOverrideDto
{
    [JsonPropertyName("monto")]
    public decimal Monto { get; set; }

    [JsonPropertyName("monto_venta")]
    public decimal MontoVenta { get; set; } = 0m;
}

// POST /api/terminal/cancelacion -> { "transaccion_id": "TX-88321" }
// Si transaccion_id es una Referencia Payworks numerica de 12 digitos y se
// envia "monto" > 0, el servicio intenta la devolucion financiera real
// (DEVOLUCION via BanortePayworksWrapper) en lugar del reset de sesion.
public class TerminalCancelacionRequestDto : TerminalConfigOverrideDto
{
    [JsonPropertyName("transaccion_id")]
    public string TransaccionId { get; set; } = string.Empty;

    [JsonPropertyName("monto")]
    public decimal? Monto { get; set; }
}

// POST /api/terminal/verificar -> { "numero_control": "10143022B" }
public class TerminalVerificarRequestDto : TerminalConfigOverrideDto
{
    [JsonPropertyName("numero_control")]
    public string NumeroControl { get; set; } = string.Empty;
}

// Respuesta unificada: los 18 campos del Wrapper + RawResponse + traza
public class TerminalTransaccionResponseDto
{
    public bool Aprobada { get; set; }
    public string Estado { get; set; } = string.Empty;
    public string CodigoRespuesta { get; set; } = string.Empty;
    public string MensajeRespuesta { get; set; } = string.Empty;
    public string CodigoAutorizacion { get; set; } = string.Empty;
    public string ReferenciaPayworks { get; set; } = string.Empty;

    // Eco de la "referencia" que envio el cliente en /cobro (numControl
    // interno siempre lo genera el worker: ddHHmmss + sufijo).
    public string ReferenciaCliente { get; set; } = string.Empty;

    // Identificacion de con que caja/afiliacion se proceso la operacion
    // (resueltos desde t_banorte_usuarios u overrides).
    public int Sucursal { get; set; }
    public int Caja { get; set; }
    public string IpTerminal { get; set; } = string.Empty;
    public string IdTerminal { get; set; } = string.Empty;
    public string Afiliacion { get; set; } = string.Empty;

    public string NumControl { get; set; } = string.Empty;
    public string UuidSesion { get; set; } = string.Empty;
    public decimal Monto { get; set; }
    public decimal MontoEfectivo { get; set; }
    public string VoucherTexto { get; set; } = string.Empty;
    public string Bin { get; set; } = string.Empty;
    public string Pan { get; set; } = string.Empty;
    public string TarjetaEnmascarada { get; set; } = string.Empty;
    public string ModoEntrada { get; set; } = string.Empty;
    public string UsoPin { get; set; } = string.Empty;
    public string TipoTarjeta { get; set; } = string.Empty;
    public string MarcaTarjeta { get; set; } = string.Empty;
    public string BancoEmisor { get; set; } = string.Empty;
    public string FechaExpiracion { get; set; } = string.Empty;
    public string EmvAid { get; set; } = string.Empty;
    public string EmvTvr { get; set; } = string.Empty;
    public string EmvPrefName { get; set; } = string.Empty;
    public string EmvLabel { get; set; } = string.Empty;
    public string RawResponse { get; set; } = string.Empty;
    public string UltimaTraza { get; set; } = string.Empty;
}

// Credenciales ya resueltas para operar la terminal (ConfiguracionCajaService
// -> TerminalService). Nunca se serializa al cliente: trae Password.
public class TerminalCredencialesDto
{
    public string IpTerminal { get; set; } = string.Empty;
    public int Puerto { get; set; }
    public string PuertoStr { get; set; } = "1818";
    public string Usuario { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Modo { get; set; } = "AUT";
    public string Afiliacion { get; set; } = string.Empty;
    public string IdTerminal { get; set; } = string.Empty;
    public int Sucursal { get; set; }
    public int Caja { get; set; }
    public string Origen { get; set; } = string.Empty;
}

// GET /api/sistema/cajas: resumen consolidado por caja (impresoras + terminal)
public class RecursoCajaDto
{
    public string Recurso { get; set; } = string.Empty;
    public string NombreDispositivoLocal { get; set; } = string.Empty;
    public bool InstaladaLocal { get; set; }
}

public class CajaResumenDto
{
    public int Caja { get; set; }
    public List<RecursoCajaDto> Impresoras { get; set; } = new();
    public TerminalCajaDto? Terminal { get; set; }
    public string? IpPinpad { get; set; }
}

// GET /api/terminal/cajas?sucursal=N -> filas de t_banorte_usuarios (sin pass)
public class TerminalCajaDto
{
    public int Cia { get; set; }
    public int Sucursal { get; set; }
    public int Caja { get; set; }
    public string IdTerminal { get; set; } = string.Empty;
    public string Puerto { get; set; } = string.Empty;
    public string Afiliacion { get; set; } = string.Empty;
    public string Usuario { get; set; } = string.Empty;
    public string Modo { get; set; } = string.Empty;
    public string Origen { get; set; } = string.Empty;
}


