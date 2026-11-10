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
    [JsonPropertyName("recurso")]
    public string Recurso { get; set; } = string.Empty;

    [JsonPropertyName("ticket_dataset")]
    public List<TicketLineaDto> TicketDataset { get; set; } = new();
}

// Contrato del docx: { "url_file": "https://empresa.com/doc.pdf", "recurso": "HP_LaserJet_Oficina" }
public class DocumentoRequestDto
{
    [JsonPropertyName("url_file")]
    public string UrlFile { get; set; } = string.Empty;

    [JsonPropertyName("recurso")]
    public string Recurso { get; set; } = string.Empty;
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


