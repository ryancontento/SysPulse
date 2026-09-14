using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using SysPulse.Core.Models;

namespace SysPulse.Core.Collectors;

/// <summary>
/// Attributes network traffic to processes by listening to the kernel TCP/IP ETW provider.
/// Requires elevation.
/// </summary>
internal sealed class ProcessNetworkCollector : IDisposable
{
    // A private session name (kernel providers are allowed in named sessions since Windows 8) instead of the
    // machine-wide "NT Kernel Logger", which PerfView, WPR, and similar tools rely on. If an earlier SysPulse
    // run crashed and left this session behind, TraceEvent closes and reopens it.
    private const string SessionName = "SysPulse-ProcessNetwork";

    private readonly Lock _gate = new();
    private Dictionary<int, (long Received, long Sent)> _pending = new();
    private long _previousTimestamp = Stopwatch.GetTimestamp();
    private TraceEventSession? _session;
    private volatile bool _stopped;

    public bool IsAvailable => _session is not null && !_stopped;

    public void Start()
    {
        var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        _session = session; // Assigned up front so Dispose can always stop it.

        try
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var kernel = session.Source.Kernel;
            kernel.TcpIpRecv += e => Record(e.ProcessID, received: e.size, sent: 0);
            kernel.TcpIpRecvIPV6 += e => Record(e.ProcessID, received: e.size, sent: 0);
            kernel.UdpIpRecv += e => Record(e.ProcessID, received: e.size, sent: 0);
            kernel.UdpIpRecvIPV6 += e => Record(e.ProcessID, received: e.size, sent: 0);
            kernel.TcpIpSend += e => Record(e.ProcessID, received: 0, sent: e.size);
            kernel.TcpIpSendIPV6 += e => Record(e.ProcessID, received: 0, sent: e.size);
            kernel.UdpIpSend += e => Record(e.ProcessID, received: 0, sent: e.size);
            kernel.UdpIpSendIPV6 += e => Record(e.ProcessID, received: 0, sent: e.size);

            var thread = new Thread(() =>
            {
                try
                {
                    session.Source.Process(); // Blocks until the session stops.
                }
                catch (Exception)
                {
                    // Session torn down underneath us.
                }
                finally
                {
                    // Disposed by us, or stopped by something else; either way there's no more data.
                    _stopped = true;
                }
            })
            {
                IsBackground = true,
                Name = "SysPulse ETW network",
            };
            thread.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IReadOnlyDictionary<int, NetworkMetrics> Sample()
    {
        Dictionary<int, (long Received, long Sent)> totals;
        lock (_gate)
        {
            totals = _pending;
            _pending = new Dictionary<int, (long Received, long Sent)>();
        }

        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalSeconds;
        _previousTimestamp = now;

        if (seconds <= 0)
            return new Dictionary<int, NetworkMetrics>();

        return totals.ToDictionary(t => t.Key, t => new NetworkMetrics(t.Value.Received / seconds, t.Value.Sent / seconds));
    }

    private void Record(int processId, int received, int sent)
    {
        lock (_gate)
        {
            var current = _pending.GetValueOrDefault(processId);
            _pending[processId] = (current.Received + received, current.Sent + sent);
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
