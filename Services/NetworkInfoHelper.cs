using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;

namespace berllanoWorker.Services;

// Datos de identidad de esta PC en la LAN. Lo usan SistemaController
// (health check para el front) y HeartbeatService (auto-registro en la API central).
public static class NetworkInfoHelper
{
    private static readonly string[] MarcadoresVirtual =
    [
        "virtual", "vmware", "hyper-v", "vethernet", "virtualbox",
        "tap-", "tailscale", "zerotier", "wsl", "docker", "vpn"
    ];

    public static List<string> ObtenerIpsLan()
    {
        var ips = new List<string>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            var tipo = ni.NetworkInterfaceType;
            if (tipo == NetworkInterfaceType.Loopback
                || tipo == NetworkInterfaceType.Tunnel
                || tipo == NetworkInterfaceType.Ppp)
                continue;

            var descripcion = (ni.Description + " " + ni.Name).ToLowerInvariant();
            if (MarcadoresVirtual.Any(m => descripcion.Contains(m)))
                continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                var ip = addr.Address.ToString();
                if (ip.StartsWith("169.254.")) // APIPA = sin DHCP, inutil para enrutar
                    continue;

                ips.Add(ip);
            }
        }

        return ips;
    }

    public static string ObtenerHostname() => Environment.MachineName;

    public static string ObtenerHostMdns() => $"{Environment.MachineName.ToLowerInvariant()}.local";

    public static string ObtenerVersion() =>
        typeof(NetworkInfoHelper).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "1.0.0";
}
