using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Stenor.Interop;

namespace Stenor.Services;

/// <summary>
/// Startup network-stack guard. Some consumer networks advertise IPv6 (router hands out an
/// address and default route) while actually blackholing IPv6 traffic. .NET has no
/// Happy-Eyeballs fallback: it walks every AAAA answer sequentially with a ~21 s OS connect
/// timeout each before reaching an IPv4 address — far past every request timeout in this app,
/// so batch transcription, live typing, the key test and update checks all appear dead.
/// The guard TCP-probes the Gemini host over both families and, whenever IPv4 works on a
/// dual-stack answer, sets the process-wide "System.Net.DisableIPv6" AppContext switch. IPv4
/// is preferred even when the IPv6 probe passes: the switch is latched after first socket use,
/// so a network whose IPv6 degrades mid-session (observed 2026-07-16: probe OK at startup,
/// blackholed two hours later) would otherwise leave the app dead until restart, and nothing
/// this app talks to needs IPv6.
/// Scope note: Gemini REST calls no longer depend on this guard — they dial IPv4-first per
/// connection (GeminiClientProvider.CreateIpv4FirstHttpClient). The guard still matters for
/// the transports that accept no custom connect logic: the Gemini Live WebSocket and the
/// Velopack update check.
/// It must run before anything creates a managed socket (HttpClient, ClientWebSocket, the
/// Velopack update check): the runtime latches the switch on first socket use — which is also
/// why the probe itself speaks raw Winsock instead of System.Net.Sockets. Managed DNS is safe:
/// it resolves through a different code path that does not latch the switch (verified on
/// .NET 10).
/// </summary>
internal static class NetworkGuard
{
    private const string ProbeHost = "generativelanguage.googleapis.com";
    private const int ProbePort = 443;
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Outcome of the startup check; logged once the Logger exists.</summary>
    public static string Summary { get; private set; } = "not run";

    /// <summary>Never throws: on any failure the process keeps .NET's default behavior.</summary>
    public static void ApplyIpv4FallbackIfNeeded()
    {
        try
        {
            var resolve = Dns.GetHostAddressesAsync(ProbeHost);
            if (!resolve.Wait(DnsTimeout))
            {
                Summary = "DNS probe timed out; defaults kept";
                return;
            }
            var addresses = resolve.Result;
            var v6 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetworkV6);
            var v4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
            if (v6 is null || v4 is null)
            {
                Summary = "single-stack network; defaults kept";
                return;
            }

            // On a healthy network both probes return in well under a second; the full
            // ProbeTimeout is only ever paid when something is actually broken.
            var v6Probe = Task.Run(() => CanConnectTcp(v6, ProbePort, ProbeTimeout));
            var v4Probe = Task.Run(() => CanConnectTcp(v4, ProbePort, ProbeTimeout));
            Task.WaitAll(v6Probe, v4Probe);

            if (v4Probe.Result)
            {
                AppContext.SetSwitch("System.Net.DisableIPv6", true);
                Summary = v6Probe.Result
                    ? "IPv4 OK (IPv6 also OK); IPv4-only for this session"
                    : "IPv6 unreachable, IPv4 OK; IPv4-only for this session";
                return;
            }
            Summary = $"IPv4 unreachable (IPv6={v6Probe.Result}); defaults kept";
        }
        catch (Exception ex)
        {
            Summary = $"probe failed ({ex.GetType().Name}); defaults kept";
        }
    }

    /// <summary>Non-blocking raw-Winsock connect + select, bounded by <paramref name="timeout"/>.</summary>
    private static bool CanConnectTcp(IPAddress address, int port, TimeSpan timeout)
    {
        var wsaData = new byte[512]; // WSADATA is ~400 bytes on x64; contents are irrelevant
        if (NativeMethods.WSAStartup(0x0202, wsaData) != 0)
        {
            return false;
        }
        try
        {
            var family = address.AddressFamily == AddressFamily.InterNetworkV6
                ? NativeMethods.AF_INET6
                : NativeMethods.AF_INET;
            var socket = NativeMethods.socket(family, NativeMethods.SOCK_STREAM, NativeMethods.IPPROTO_TCP);
            if (socket == NativeMethods.INVALID_SOCKET)
            {
                return false;
            }
            try
            {
                var nonBlocking = 1u;
                if (NativeMethods.ioctlsocket(socket, NativeMethods.FIONBIO, ref nonBlocking) != 0)
                {
                    return false;
                }
                var sockaddr = BuildSockaddr(address, port);
                var rc = NativeMethods.connect(socket, sockaddr, sockaddr.Length);
                if (rc == 0)
                {
                    return true; // connected synchronously
                }
                if (rc == NativeMethods.SOCKET_ERROR
                    && Marshal.GetLastPInvokeError() != NativeMethods.WSAEWOULDBLOCK)
                {
                    return false;
                }

                var write = new NativeMethods.FD_SET_SINGLE { fd_count = 1, fd_socket = socket };
                var except = new NativeMethods.FD_SET_SINGLE { fd_count = 1, fd_socket = socket };
                var tv = new NativeMethods.TIMEVAL
                {
                    tv_sec = (int)timeout.TotalSeconds,
                    tv_usec = (int)(timeout.TotalMilliseconds % 1000 * 1000),
                };
                var ready = NativeMethods.select(0, 0, ref write, ref except, ref tv);
                return ready > 0 && write.fd_count > 0;
            }
            finally
            {
                _ = NativeMethods.closesocket(socket);
            }
        }
        finally
        {
            _ = NativeMethods.WSACleanup();
        }
    }

    private static byte[] BuildSockaddr(IPAddress address, int port)
    {
        var addressBytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var sa = new byte[28]; // sockaddr_in6: family, port, flowinfo, address, scope id
            sa[0] = NativeMethods.AF_INET6;
            sa[2] = (byte)(port >> 8);
            sa[3] = (byte)port;
            addressBytes.CopyTo(sa, 8);
            BitConverter.GetBytes((uint)address.ScopeId).CopyTo(sa, 24);
            return sa;
        }
        var sa4 = new byte[16]; // sockaddr_in: family, port, address, zero padding
        sa4[0] = NativeMethods.AF_INET;
        sa4[2] = (byte)(port >> 8);
        sa4[3] = (byte)port;
        addressBytes.CopyTo(sa4, 4);
        return sa4;
    }
}
