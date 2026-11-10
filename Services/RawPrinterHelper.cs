using System.Runtime.InteropServices;

namespace berllanoWorker.Services;

// Envio de bytes crudos al spooler de Windows (patron clasico de
// Microsoft KB322091). RAW = los bytes ESC/POS llegan tal cual a la
// impresora, sin pasar por un driver de formato.
public static class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)]
        public string pDocName = string.Empty;
        [MarshalAs(UnmanagedType.LPStr)]
        public string pOutputFile = string.Empty;
        [MarshalAs(UnmanagedType.LPStr)]
        public string pDataType = string.Empty;
    }

    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPStr)] string szPrinter, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOA di);

    [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public static bool SendBytesToPrinter(string szPrinterName, byte[] bytes)
    {
        if (bytes.Length == 0)
            return true;

        var pBytes = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pBytes, bytes.Length);
            return SendBytesToPrinter(szPrinterName, pBytes, bytes.Length);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pBytes);
        }
    }

    private static bool SendBytesToPrinter(string szPrinterName, IntPtr pBytes, int dwCount)
    {
        var hPrinter = IntPtr.Zero;
        var di = new DOCINFOA { pDocName = "berllanoWorker Ticket", pDataType = "RAW" };

        try
        {
            if (!OpenPrinter(szPrinterName.Normalize(), out hPrinter, IntPtr.Zero))
                return false;

            if (StartDocPrinter(hPrinter, 1, di))
            {
                if (StartPagePrinter(hPrinter))
                {
                    WritePrinter(hPrinter, pBytes, dwCount, out _);
                    EndPagePrinter(hPrinter);
                }
                EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            if (hPrinter != IntPtr.Zero)
                ClosePrinter(hPrinter);
        }

        return Marshal.GetLastWin32Error() == 0;
    }
}
