using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BGSim.Server;

/// <summary>
/// <c>BGSim.Server --healthcheck</c>: GET /health on this machine and exit 0 when it answers 200, else 1. For the Docker
/// HEALTHCHECK of a chiseled image, which has no shell and no curl. A raw socket rather than HttpClient keeps the HTTP client
/// stack (and TLS) out of the binary.
/// </summary>
internal static class HealthCheck
{
    public static async Task<int> RunAsync(TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port()), cancel.Token);
            await socket.SendAsync("GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"u8.ToArray(), SocketFlags.None, cancel.Token);
            var buffer = new byte[64];
            var read = 0;
            while (read < 12)
            {
                var got = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, cancel.Token);
                if (got == 0)
                {
                    break;
                }

                read += got;
            }

            // "HTTP/1.1 200 ..."
            var status = Encoding.ASCII.GetString(buffer, 0, read);
            return status.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) ? 0 : 1;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return 1;
        }
    }

    /// <summary>The first port of ASPNETCORE_HTTP_PORTS (the image sets 8080), else 8080.</summary>
    private static int Port()
    {
        var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? string.Empty;
        var first = ports.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : 8080;
    }
}
