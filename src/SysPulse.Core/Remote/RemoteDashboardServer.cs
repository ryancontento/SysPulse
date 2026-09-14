using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SysPulse.Core.Collectors;
using SysPulse.Core.Models;
using SysPulse.Core.Power;
using SysPulse.Core.Recording;
using SysPulse.Core.Rules;
using SysPulse.Core.Services;
using SysPulse.Core.Settings;

namespace SysPulse.Core.Remote;

public enum RemoteState
{
    Off,

    /// <summary>Listening on the local network.</summary>
    Running,

    /// <summary>Listening on this PC only, because the one-time setup hasn't been done.</summary>
    LocalOnly,

    /// <summary>Turned on, but not listening because this isn't a trusted network.</summary>
    Paused,

    Failed,
}

public interface IRemoteDashboard
{
    RemoteState State { get; }

    string? Error { get; }

    int Port { get; }

    /// <summary>Phones currently receiving live updates.</summary>
    int ConnectedPhones { get; }

    DateTimeOffset? LastSeen { get; }

    /// <summary>Raised on a background thread when the state or connection count changes.</summary>
    event Action? StatusChanged;

    /// <summary>Dashboard URLs for this PC's local network addresses.</summary>
    IReadOnlyList<string> Addresses();

    /// <summary>Stops and starts the listener, e.g. after the one-time setup.</summary>
    void Restart();

    /// <summary>Networks this PC is connected to, as of the last check.</summary>
    IReadOnlyList<NetworkInfo> ConnectedNetworks { get; }

    /// <summary>Identifies the connected networks again shortly, instead of waiting for the next periodic check.</summary>
    void CheckNetworks();
}

/// <summary>
/// Serves the read-only phone dashboard over HTTP on the local network, using http.sys (HttpListener) so it
/// doesn't need the ASP.NET Core runtime. Every data request needs the pairing key.
/// </summary>
public sealed partial class RemoteDashboardServer(
    IMetricsSource metrics,
    ISettingsService settings,
    IFlightRecorder recorder,
    IRuleEngine rules,
    IPowerProfileService power,
    ILogger<RemoteDashboardServer> logger) : IRemoteDashboard, IHostedService, IDisposable
{
    private const int MaxStreams = 8;
    private const int MaxHistoryPoints = 300;
    private const int MaxFailedAttempts = 20;
    private const string KeyHeader = "X-SysPulse-Key";
    private const string PageResource = "SysPulse.Remote.phone.html";

    private const string Manifest = """
        {"name":"SysPulse","short_name":"SysPulse","start_url":"/","display":"standalone","background_color":"#13120f","theme_color":"#13120f"}
        """;

    // No external resources; the page's script and styles are inline, and all data goes through textContent.
    private const string PagePolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; font-src 'self'; connect-src 'self'; " +
        "img-src 'self' data:; manifest-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(10);

    // Network changes raise an event, but check periodically too in case one is missed (e.g. after sleep).
    private static readonly TimeSpan NetworkRecheck = TimeSpan.FromSeconds(30);

    // After a change, give DHCP and the ARP cache a moment to settle before identifying the router.
    private static readonly TimeSpan NetworkSettleDelay = TimeSpan.FromSeconds(3);
    private static readonly Lazy<byte[]> Page = new(LoadPage);

    private readonly Lock _lock = new();
    private readonly List<Channel<string>> _streams = [];
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> _failures = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _listenerCancellation;
    private CancellationTokenSource _streamCancellation = new();
    private string _streamKey = "";
    private string? _latestSnapshot;
    private int _connectedPhones;
    private bool _disposed;

    private readonly SemaphoreSlim _networkSignal = new(0);
    private CancellationTokenSource? _monitorCancellation;
    private IReadOnlyList<NetworkInfo> _networks = [];
    private long _lastSeenTicks;

    public RemoteState State { get; private set; }

    public string? Error { get; private set; }

    public int Port { get; private set; }

    public int ConnectedPhones => Volatile.Read(ref _connectedPhones);

    public DateTimeOffset? LastSeen =>
        Interlocked.Read(ref _lastSeenTicks) is var ticks and > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime() : null;

    public event Action? StatusChanged;

    public IReadOnlyList<NetworkInfo> ConnectedNetworks
    {
        get
        {
            lock (_lock)
                return _networks;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        settings.Changed += OnSettingsChanged;
        metrics.Updated += OnMetricsUpdated;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        // Identifying the router can take a moment, so it runs in the background. Until the first check finishes,
        // a dashboard limited to trusted networks stays paused.
        _monitorCancellation = new CancellationTokenSource();
        var token = _monitorCancellation.Token;
        _ = Task.Run(() => MonitorNetworksAsync(token), CancellationToken.None);

        Apply(settings.Current, force: false);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnSettingsChanged;
        metrics.Updated -= OnMetricsUpdated;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _monitorCancellation?.Cancel();

        lock (_lock)
            StopListener();

        return Task.CompletedTask;
    }

    public void Restart() => Apply(settings.Current, force: true);

    public void CheckNetworks()
    {
        try
        {
            _networkSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => CheckNetworks();

    private async Task MonitorNetworksAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                RefreshNetworks();
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Couldn't identify the current network");
            }

            try
            {
                if (await _networkSignal.WaitAsync(NetworkRecheck, token))
                {
                    await Task.Delay(NetworkSettleDelay, token);

                    // Several changes usually arrive together; one check covers them all.
                    while (_networkSignal.Wait(0))
                    {
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private void RefreshNetworks()
    {
        var networks = NetworkIdentity.Connected();

        bool changed;
        lock (_lock)
        {
            changed = !networks.Select(n => n.Id).SequenceEqual(_networks.Select(n => n.Id));
            _networks = networks;
        }

        // Trust on first use: the network phone access is first used on is almost always home.
        var current = settings.Current;
        if (current is { RemoteEnabled: true, RemoteOnlyTrustedNetworks: true, RemoteTrustedNetworks.Count: 0 } && networks.Count > 0)
        {
            settings.Update(s => s with { RemoteTrustedNetworks = [.. s.RemoteTrustedNetworks, .. networks] });
            return; // The settings change re-applies.
        }

        Apply(current, force: false);

        if (changed)
            StatusChanged?.Invoke();
    }

    private static bool IsTrusted(IReadOnlyList<NetworkInfo> trusted, IReadOnlyList<NetworkInfo> connected) =>
        connected.Count > 0 && connected.All(network => trusted.Any(t => t.Id == network.Id));

    public IReadOnlyList<string> Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(NetworkCollector.IsPhysical)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address)
                    && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                // The configured port, so the list is right even before the listener has started (or while paused).
                .Select(address => $"http://{address}:{settings.Current.RemotePort}/")
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private void OnSettingsChanged(AppSettings updated)
    {
        Apply(updated, force: false);

        // Just turned on with no trusted networks yet: identify this one now rather than at the next periodic check.
        if (updated is { RemoteEnabled: true, RemoteOnlyTrustedNetworks: true, RemoteTrustedNetworks.Count: 0 })
            CheckNetworks();
    }

    private void Apply(AppSettings current, bool force)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            // A new key disconnects every phone paired with the old one.
            if (current.RemoteKey != _streamKey)
            {
                _streamKey = current.RemoteKey;
                _streamCancellation.Cancel();
                _streamCancellation.Dispose();
                _streamCancellation = new CancellationTokenSource();
            }

            var trusted = !current.RemoteOnlyTrustedNetworks || IsTrusted(current.RemoteTrustedNetworks, _networks);
            var wanted = current.RemoteEnabled && trusted ? current.RemotePort : (int?)null;
            var idle = current.RemoteEnabled ? RemoteState.Paused : RemoteState.Off;
            var listening = State is RemoteState.Running or RemoteState.LocalOnly;

            // Nothing to do: already idle in the right way, or already listening (or failed) on the wanted port.
            // A failed port is only retried on Restart or a port change, not on every network check.
            if (!force && ((wanted is null && State == idle) || (wanted == Port && (listening || State == RemoteState.Failed))))
                return;

            StopListener();
            if (wanted is { } port)
                StartListener(port);
            else
                State = idle;
        }

        StatusChanged?.Invoke();
    }

    private void StartListener(int port)
    {
        Port = port;
        Error = null;

        // Listening on all addresses needs a URL reservation (the one-time setup) unless we're elevated.
        // Without one, still serve this PC so the page can be previewed.
        if (TryStart($"http://+:{port}/", out var listener, out var error))
        {
            State = RemoteState.Running;
        }
        else if (error?.ErrorCode == 5 && TryStart($"http://localhost:{port}/", out listener, out error))
        {
            State = RemoteState.LocalOnly;
        }
        else
        {
            State = RemoteState.Failed;
            Error = error?.ErrorCode is 32 or 183
                ? $"Port {port} is already in use by another app. Pick a different port."
                : error?.Message ?? "Couldn't start the phone dashboard.";
            logger.LogWarning(error, "Phone dashboard couldn't start on port {Port}", port);
            return;
        }

        _listener = listener;
        _listenerCancellation = new CancellationTokenSource();
        _ = AcceptLoopAsync(listener!, _listenerCancellation.Token);
    }

    private static bool TryStart(string prefix, out HttpListener? listener, out HttpListenerException? error)
    {
        listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
            error = null;
            return true;
        }
        catch (HttpListenerException e)
        {
            listener.Close();
            listener = null;
            error = e;
            return false;
        }
    }

    private void StopListener()
    {
        _listenerCancellation?.Cancel();
        _listenerCancellation?.Dispose();
        _listenerCancellation = null;

        _streamCancellation.Cancel();
        _streamCancellation.Dispose();
        _streamCancellation = new CancellationTokenSource();

        _listener?.Close();
        _listener = null;
        State = RemoteState.Off;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break; // Listener closed.
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Cache-Control"] = "no-store";

            // Defense in depth on top of the private-network firewall rule: ignore anything routed in from outside.
            if (request.RemoteEndPoint?.Address is not { } remote || !IsLocalNetwork(remote))
            {
                response.StatusCode = 403;
                return;
            }

            if (request.HttpMethod != "GET")
            {
                response.StatusCode = 405;
                return;
            }

            var path = request.Url?.AbsolutePath ?? "/";
            switch (path)
            {
                case "/" or "/index.html":
                    response.Headers["Content-Security-Policy"] = PagePolicy;
                    await WriteAsync(response, Page.Value, "text/html; charset=utf-8");
                    return;
                case "/manifest.webmanifest":
                    await WriteAsync(response, Encoding.UTF8.GetBytes(Manifest), "application/manifest+json");
                    return;
            }

            if (path.StartsWith("/fonts/", StringComparison.Ordinal))
            {
                await WriteFontAsync(response, path["/fonts/".Length..]);
                return;
            }

            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                response.StatusCode = 404;
                return;
            }

            var client = remote.ToString();
            if (IsLockedOut(client))
            {
                response.StatusCode = 429;
                return;
            }

            if (!IsAuthorized(request))
            {
                RecordFailure(client);
                response.StatusCode = 401;
                return;
            }

            Interlocked.Exchange(ref _lastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);

            switch (path)
            {
                case "/api/snapshot":
                    await WriteAsync(response, Encoding.UTF8.GetBytes(_latestSnapshot ?? BuildSnapshot(metrics.Current)), "application/json");
                    break;
                case "/api/history":
                    var minutes = int.TryParse(request.QueryString["minutes"], out var m) ? Math.Clamp(m, 1, 60) : 5;
                    var history = RemotePayload.History(recorder.GetSamples(TimeSpan.FromMinutes(minutes)), MaxHistoryPoints);
                    await WriteAsync(response, Encoding.UTF8.GetBytes(history), "application/json");
                    break;
                case "/api/stream":
                    await StreamAsync(response);
                    break;
                default:
                    response.StatusCode = 404;
                    break;
            }
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The phone went away or the server is stopping.
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Phone dashboard request failed");
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    private async Task StreamAsync(HttpListenerResponse response)
    {
        if (Interlocked.Increment(ref _connectedPhones) > MaxStreams)
        {
            Interlocked.Decrement(ref _connectedPhones);
            response.StatusCode = 503;
            return;
        }

        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        CancellationToken token;
        lock (_lock)
        {
            _streams.Add(channel);
            token = _streamCancellation.Token;
        }

        StatusChanged?.Invoke();

        try
        {
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
            var output = response.OutputStream;

            await WriteEventAsync(output, "retry: 3000\n\n", token);
            if (_latestSnapshot is { } first)
                await WriteEventAsync(output, $"data: {first}\n\n", token);

            await foreach (var json in channel.Reader.ReadAllAsync(token))
            {
                await WriteEventAsync(output, $"data: {json}\n\n", token);
                Interlocked.Exchange(ref _lastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);
            }
        }
        finally
        {
            lock (_lock)
                _streams.Remove(channel);

            Interlocked.Decrement(ref _connectedPhones);
            StatusChanged?.Invoke();
        }
    }

    private static async Task WriteEventAsync(Stream output, string text, CancellationToken token)
    {
        await output.WriteAsync(Encoding.UTF8.GetBytes(text), token);
        await output.FlushAsync(token);
    }

    private void OnMetricsUpdated(SystemSnapshot snapshot)
    {
        Channel<string>[] streams;
        lock (_lock)
        {
            if (State == RemoteState.Off)
            {
                _latestSnapshot = null;
                return;
            }

            streams = [.. _streams];
        }

        var json = BuildSnapshot(snapshot);
        _latestSnapshot = json;

        foreach (var stream in streams)
            stream.Writer.TryWrite(json);
    }

    private string BuildSnapshot(SystemSnapshot snapshot)
    {
        PowerProfile? profile;
        try
        {
            profile = power.IsSupported ? power.Current : null;
        }
        catch (Exception)
        {
            profile = null;
        }

        var current = settings.Current;
        return RemotePayload.Snapshot(
            snapshot,
            current,
            profile,
            rules.RecentActivity,
            current.Rules.Any(r => rules.IsActive(r.Id)));
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var expected = settings.Current.RemoteKey;
        var supplied = request.Headers[KeyHeader] ?? request.QueryString["key"];
        if (expected.Length == 0 || supplied is null)
            return false;

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    }

    private bool IsLockedOut(string client) =>
        _failures.TryGetValue(client, out var failures)
        && failures.Count >= MaxFailedAttempts
        && DateTimeOffset.UtcNow - failures.Since < LockoutWindow;

    private void RecordFailure(string client)
    {
        var now = DateTimeOffset.UtcNow;
        _failures.AddOrUpdate(
            client,
            _ => (1, now),
            (_, failures) => now - failures.Since > LockoutWindow ? (1, now) : (failures.Count + 1, failures.Since));
    }

    // Loopback, private IPv4 ranges, IPv4 link-local, IPv6 unique-local and link-local.
    private static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static async Task WriteAsync(HttpListenerResponse response, byte[] body, string contentType)
    {
        response.ContentType = contentType;
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
    }

    // Reuses the app's bundled IBM Plex fonts from wwwroot/fonts.
    private static async Task WriteFontAsync(HttpListenerResponse response, string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "fonts", name);
        if (!FontName().IsMatch(name) || !File.Exists(path))
        {
            response.StatusCode = 404;
            return;
        }

        response.Headers["Cache-Control"] = "public, max-age=86400";
        await WriteAsync(response, await File.ReadAllBytesAsync(path), "font/woff2");
    }

    private static byte[] LoadPage()
    {
        using var stream = typeof(RemoteDashboardServer).Assembly.GetManifestResourceStream(PageResource)
            ?? throw new InvalidOperationException("The phone dashboard page is missing from the build.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [GeneratedRegex("^[a-z0-9-]+\\.woff2$")]
    private static partial Regex FontName();

    // The container can dispose this more than once: it's registered as itself, as IRemoteDashboard, and as a hosted service.
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _monitorCancellation?.Cancel();
            _monitorCancellation?.Dispose();
            StopListener();
            _streamCancellation.Dispose();
            _networkSignal.Dispose();
        }
    }
}
