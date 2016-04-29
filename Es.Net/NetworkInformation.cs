using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Es.Net
{
    public static class NetworkInformation
    {
        private static readonly IPAddress LocalLoopbackIpAddress = new IPAddress(new byte[] {127, 0, 0, 1});

        public static IPAddress LocalIpAddress()
        {
            IPAddress ipAddress = null;
            var ips = Dns.GetHostAddresses(Dns.GetHostName());

            foreach (var ip in ips.Where(x => x.AddressFamily == AddressFamily.InterNetwork))
            {
                var bytes = ip.GetAddressBytes();
                if (bytes.Length != 4)
                    continue;

                if (bytes[0] == 10)
                {
                    ipAddress = ip;
                    break;
                }
                if (bytes[0] == 172 && 16 <= bytes[1] && bytes[1] <= 31)
                {
                    ipAddress = ip;
                    break;
                }
                if (bytes[0] == 192 && bytes[1] == 168)
                {
                    ipAddress = ip;
                    break;
                }
            }
            return ipAddress ?? LocalLoopbackIpAddress;
        }
    }
}