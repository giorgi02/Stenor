using System.Net;
using System.Net.Sockets;
using Google.GenAI;
using Google.GenAI.Types;

namespace Stenor.Services;

/// <summary>
/// Caches a single Google.GenAI <see cref="Client"/> keyed on the current API key, shared by
/// the batch and live transcription services. Invalidated whenever settings change so a new
/// key takes effect immediately. The API key itself is never logged.
/// Clients are built with <see cref="Ipv4FirstClientOptions"/> so every REST connection dials
/// IPv4 before IPv6 — see <see cref="CreateIpv4FirstHttpClient"/> for why.
/// </summary>
public sealed class GeminiClientProvider : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly object _sync = new();
    private Client? _client;
    private string? _clientKey;

    public GeminiClientProvider(SettingsStore settings)
    {
        _settings = settings;
        _settings.Changed += InvalidateClient;
    }

    /// <summary>Returns the cached client, or null when no API key is configured.</summary>
    public Client? GetClient()
    {
        var key = _settings.GetApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        lock (_sync)
        {
            if (_client is null || _clientKey != key)
            {
                // The replaced client is dropped, never disposed: a transcription may still be
                // mid-request on it, and disposing the underlying HttpClient aborts that call.
                // The GC reclaims it once the in-flight request completes.
                _client = new Client(apiKey: key, clientOptions: Ipv4FirstClientOptions);
                _clientKey = key;
            }
            return _client;
        }
    }

    /// <summary>SDK options every Gemini REST client must be built with (including the
    /// throwaway client in the key test).</summary>
    internal static readonly ClientOptions Ipv4FirstClientOptions = new()
    {
        HttpClientFactory = CreateIpv4FirstHttpClient,
    };

    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Address family that most recently carried a successful connection; dialed
    /// first on the next one, so a broken family pays the <see cref="ConnectAttemptTimeout"/>
    /// once per network change — not on every connection. IPv4 is the initial preference
    /// (the networks this app has met break IPv6, never IPv4). Racy updates are benign.</summary>
    private static volatile AddressFamily s_preferredFamily = AddressFamily.InterNetwork;

    /// <summary>
    /// HttpClient whose connections dial the last-known-good address family first (IPv4
    /// initially), each attempt capped at <see cref="ConnectAttemptTimeout"/>. .NET has no
    /// Happy Eyeballs: by default it walks DNS answers in resolver order (IPv6 first) with a
    /// ~21 s OS connect timeout each, so a network that blackholes IPv6 stalls every request
    /// past its deadline. The process-wide DisableIPv6 switch (NetworkGuard) latches at first
    /// socket use and therefore only helps when the breakage predates startup — observed
    /// 2026-07-16: IPv6 passed the startup probe, then blackholed mid-session and stranded
    /// the app. This callback re-decides per connection instead, and pooled connections are
    /// recycled so a network change cannot pin a dead path for long.
    /// </summary>
    private static HttpClient CreateIpv4FirstHttpClient() => new(new SocketsHttpHandler
    {
        ConnectCallback = ConnectIpv4FirstAsync,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    private static async ValueTask<Stream> ConnectIpv4FirstAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        Exception? lastFailure = null;
        foreach (var address in OrderPreferredFirst(addresses, s_preferredFamily))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true, // matches SocketsHttpHandler's built-in connect path
            };
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(ConnectAttemptTimeout);
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), attempt.Token)
                    .ConfigureAwait(false);
                s_preferredFamily = address.AddressFamily;
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                ct.ThrowIfCancellationRequested(); // caller deadline - stop; else try next address
                lastFailure = ex;
            }
        }
        throw lastFailure ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>Interleaves the families starting with the preferred one, so a healthy
    /// preferred family connects immediately and a broken one costs one attempt before the
    /// other family is tried — never a whole family's worth of timeouts.</summary>
    private static IEnumerable<IPAddress> OrderPreferredFirst(IPAddress[] addresses, AddressFamily preferred)
    {
        var first = addresses.Where(a => a.AddressFamily == preferred).ToArray();
        var rest = addresses.Where(a => a.AddressFamily != preferred).ToArray();
        for (var i = 0; i < Math.Max(first.Length, rest.Length); i++)
        {
            if (i < first.Length)
            {
                yield return first[i];
            }
            if (i < rest.Length)
            {
                yield return rest[i];
            }
        }
    }

    private void InvalidateClient()
    {
        lock (_sync)
        {
            _client = null; // dropped, not disposed - see GetClient
            _clientKey = null;
        }
    }

    public void Dispose()
    {
        _settings.Changed -= InvalidateClient;
        lock (_sync)
        {
            _client?.Dispose(); // process shutdown: no request left to abort
            _client = null;
            _clientKey = null;
        }
    }
}
