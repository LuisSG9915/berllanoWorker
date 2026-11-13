using System.Text;
using berllanoWorker.Exceptions;
using berllanoWorker.Models;

namespace berllanoWorker.Services;

// Convierte el ticket_dataset del docx en bytes ESC/POS.
// El dataset viene del frontend en UTF-8; aqui se transcodifica a la
// codepage de la impresora (CP858 por defecto) y se envia ESC t para
// que la impresora interprete bien los acentos y la enie.
public static class EscPosBuilder
{
    private static readonly byte[] Init = [0x1B, 0x40];            // ESC @
    private static readonly byte[] AlignLeft = [0x1B, 0x61, 0x00]; // ESC a 0
    private static readonly byte[] AlignCenter = [0x1B, 0x61, 0x01];
    private static readonly byte[] AlignRight = [0x1B, 0x61, 0x02];
    private static readonly byte[] BoldOn = [0x1B, 0x45, 0x01];    // ESC E 1
    private static readonly byte[] BoldOff = [0x1B, 0x45, 0x00];
    private static readonly byte[] DoubleSize = [0x1B, 0x21, 0x30]; // ESC ! 0x30
    private static readonly byte[] NormalSize = [0x1B, 0x21, 0x00];
    private static readonly byte[] FeedAndFullCut = [0x1B, 0x64, 0x04, 0x1D, 0x56, 0x00]; // ESC d 4 + GS V 0
    private static readonly byte[] FeedAndPartialCut = [0x1B, 0x64, 0x04, 0x1D, 0x56, 0x01];
    private static readonly byte[] PulsoCajon = [0x1B, 0x70, 0x00, 0x19, 0xFA]; // ESC p 0 25 250

    // Mapa .NET codepage -> numero ESC t. Valores tipicos de Epson TM;
    // si la impresora usa otra tabla, ajustar este mapa.
    private static readonly Dictionary<int, byte> CodePageEscT = new()
    {
        { 437, 0 },
        { 850, 2 },
        { 860, 3 },
        { 863, 4 },
        { 865, 5 },
        { 1252, 16 },
        { 858, 19 },
    };

    public static byte[] Build(IEnumerable<TicketLineaDto> lineas, int codePage, int columnas)
    {
        if (codePage <= 0)
            throw new HardwareValidationException("CodePage de impresion no configurado (appsettings > Impresion:CodePage)");

        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(codePage);
        }
        catch (ArgumentException ex)
        {
            throw new HardwareValidationException($"CodePage {codePage} no soportado: {ex.Message}");
        }

        var ordenadas = lineas.OrderBy(l => l.Consecutivo).ToList();
        if (ordenadas.Count == 0)
            throw new HardwareValidationException("El ticket_dataset llego vacio");

        using var ms = new MemoryStream();
        ms.Write(Init);
        ms.Write(SeleccionCodePage(codePage));

        foreach (var linea in ordenadas)
            EscribirLinea(ms, linea, encoding);

        return ms.ToArray();
    }

    private static byte[] SeleccionCodePage(int codePage)
        => [0x1B, 0x74, CodePageEscT.TryGetValue(codePage, out var n) ? n : (byte)19];

    private static void EscribirLinea(MemoryStream ms, TicketLineaDto linea, Encoding encoding)
    {
        var data = (linea.Data ?? string.Empty).Replace("\r\n", " ").Replace("\n", " ");

        switch ((linea.Modo ?? "TEXTO_PLANO").Trim().ToUpperInvariant())
        {
            case "CENTRADO_GRANDE_NEGRITA":
                ms.Write(AlignCenter);
                ms.Write(DoubleSize);
                ms.Write(BoldOn);
                EscribirTexto(ms, data, encoding);
                ms.Write(BoldOff);
                ms.Write(NormalSize);
                ms.Write(AlignLeft);
                break;

            case "CENTRADO_NEGRITA":
                ms.Write(AlignCenter);
                ms.Write(BoldOn);
                EscribirTexto(ms, data, encoding);
                ms.Write(BoldOff);
                ms.Write(AlignLeft);
                break;

            case "CENTRADO_NORMAL":
            case "CENTRADO":
                ms.Write(AlignCenter);
                EscribirTexto(ms, data, encoding);
                ms.Write(AlignLeft);
                break;

            case "NEGRITA":
                ms.Write(BoldOn);
                EscribirTexto(ms, data, encoding);
                ms.Write(BoldOff);
                break;

            case "GRANDE_NEGRITA":
                ms.Write(DoubleSize);
                ms.Write(BoldOn);
                EscribirTexto(ms, data, encoding);
                ms.Write(BoldOff);
                ms.Write(NormalSize);
                break;

            case "DERECHA":
                ms.Write(AlignRight);
                EscribirTexto(ms, data, encoding);
                ms.Write(AlignLeft);
                break;

            case "DERECHA_NEGRITA":
                ms.Write(AlignRight);
                ms.Write(BoldOn);
                EscribirTexto(ms, data, encoding);
                ms.Write(BoldOff);
                ms.Write(AlignLeft);
                break;

            case "AVANCE":
                ms.WriteByte(0x0A);
                break;

            case "CORTE_PAPEL":
                ms.Write(FeedAndFullCut);
                break;

            case "CORTE_PARCIAL":
                ms.Write(FeedAndPartialCut);
                break;

            case "CAJON":
            case "ABRE_CAJON":
            case "ABRIR_CAJON":
                ms.Write(PulsoCajon);
                break;

            case "TEXTO_PLANO":
                EscribirTexto(ms, data, encoding);
                break;

            default:
                throw new HardwareValidationException($"Modo de linea desconocido: '{linea.Modo}'");
        }
    }

    private static void EscribirTexto(MemoryStream ms, string texto, Encoding encoding)
    {
        if (texto.Length > 0)
            ms.Write(encoding.GetBytes(texto));
        ms.WriteByte(0x0A);
    }
}
