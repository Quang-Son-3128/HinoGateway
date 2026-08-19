using System.Net;

namespace HinoGateway.Web.Services;

/// <summary>
/// Whether the browser is still talking to the gateway, and when it last was.
///
/// <para>
/// A SCADA screen has one failure mode worse than showing an error, and that is showing a number.
/// When the gateway restarts or the link drops, every page in this application keeps its last
/// rendered values on screen, unchanged and unlabelled — so an operator reads a tag that stopped
/// updating four minutes ago and believes it is current. Several pages had a <c>try/finally</c>
/// around their loads with no <c>catch</c> at all, which meant a dropped connection did not even
/// produce a message.
/// </para>
///
/// <para>
/// The state is collected by <see cref="ConnectionHttpHandler"/> rather than by the pages, so it
/// covers every call any page makes, including ones written later. Pages do not have to remember to
/// participate — which is the only way a rule like this survives.
/// </para>
/// </summary>
public sealed class GatewayConnection
{
    private readonly object _gate = new();
    private bool _online = true;
    private DateTimeOffset? _lastSuccess;
    private string? _lastError;
    private int _consecutiveFailures;

    /// <summary>Raised whenever the answer to "are we connected" changes. Marshalled by the caller.</summary>
    public event Action? Changed;

    public bool IsOnline { get { lock (_gate) return _online; } }

    /// <summary>When a call last succeeded — what the banner shows so "stale" has a number on it.</summary>
    public DateTimeOffset? LastSuccess { get { lock (_gate) return _lastSuccess; } }

    public string? LastError { get { lock (_gate) return _lastError; } }

    public void RecordSuccess()
    {
        bool changed;
        lock (_gate)
        {
            _lastSuccess = DateTimeOffset.Now;
            _consecutiveFailures = 0;
            changed = !_online;
            _online = true;
            _lastError = null;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Record a call that never reached the gateway, or that it could not answer.
    ///
    /// <para>
    /// One failure is not an outage. A single request can lose a race with a reload, and flipping the
    /// whole application to "offline" over it would train operators to ignore the banner — which
    /// would cost more than never having built it. Two in a row is a link that is actually down.
    /// </para>
    /// </summary>
    public void RecordFailure(string error)
    {
        bool changed;
        lock (_gate)
        {
            _lastError = error;
            _consecutiveFailures++;
            changed = _online && _consecutiveFailures >= 2;
            if (changed) _online = false;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Is this exception the gateway being unreachable, rather than it answering with a refusal?
    /// A 403 means the connection is fine and the caller is not allowed; treating that as an outage
    /// would put an offline banner over a working gateway.
    /// </summary>
    public static bool IsTransportFailure(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,   // DNS, refused, reset — never reached it
        TaskCanceledException => true,                        // client timeout
        OperationCanceledException => true,
        _ => false,
    };

    /// <summary>Server-side failures that mean "not available", as opposed to "no" — see above.</summary>
    public static bool IsUnavailable(HttpStatusCode status) =>
        status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
