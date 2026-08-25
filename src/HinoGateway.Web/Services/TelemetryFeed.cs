using System.Text.Json;
using HinoGateway.Core;
using HinoGateway.Web.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace HinoGateway.Web.Services;

/// <summary>
/// The live tag values, over SignalR, for any page that wants to show them.
///
/// <para>
/// This was the Live Data page's private plumbing. It is here because the Tag Database page needs the
/// same thing — a tag you have just configured shows its value where you configured it, rather than
/// sending you to another page to find out whether the address was right. Two copies of a hub
/// connection, a sample dictionary and a render throttle would have drifted apart within a month.
/// </para>
///
/// <para>
/// Owned by the page, not by the container: the hub is opened when a page that shows values appears
/// and closed when it goes away. A feed shared through DI would keep pushing every tag in the plant
/// at a browser sitting on the Access page.
/// </para>
/// </summary>
public sealed class TelemetryFeed : IAsyncDisposable
{
    /// <summary>
    /// SignalR can deliver a batch per device per second (100+/s at scale). Samples are absorbed as
    /// they arrive; the screen repaints at most ~3x/s, and only when something actually arrived.
    /// </summary>
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(300);

    private readonly NavigationManager _nav;
    private readonly ApiKeyStore _keys;
    private readonly Func<Task> _onChanged;
    private readonly Dictionary<string, TagSample> _values = new(StringComparer.Ordinal);

    private HubConnection? _hub;
    private System.Threading.Timer? _renderTimer;
    private volatile bool _dirty;

    /// <param name="onChanged">
    /// Called on the render tick when new samples have arrived. The page marshals it — typically
    /// <c>() =&gt; InvokeAsync(StateHasChanged)</c>.
    /// </param>
    public TelemetryFeed(NavigationManager nav, ApiKeyStore keys, Func<Task> onChanged)
    {
        _nav = nav;
        _keys = keys;
        _onChanged = onChanged;
    }

    public bool IsConnected { get; private set; }

    /// <summary>How many batches have arrived — the only visible proof that the link is alive.</summary>
    public long UpdateCount { get; private set; }

    /// <summary>Latest sample per <b>runtime tag path</b> (<c>TagSample.TagId</c>), not per storage id.</summary>
    public IReadOnlyDictionary<string, TagSample> Values => _values;

    public TagSample? Sample(string tagPath) =>
        tagPath is not null && _values.TryGetValue(tagPath, out var sample) ? sample : null;

    public async Task StartAsync()
    {
        _renderTimer = new System.Threading.Timer(_ =>
        {
            if (!_dirty) return;
            _dirty = false;
            _ = _onChanged();
        }, null, RenderInterval, RenderInterval);

        _hub = new HubConnectionBuilder()
            .WithUrl(_nav.ToAbsoluteUri("/hubs/telemetry"), options => options.AccessTokenProvider = _keys.GetAsync)
            .WithAutomaticReconnect()
            .Build();

        _hub.On<List<TagSample>>("TagsUpdated", Apply);
        _hub.Reconnected += _ => Connected(true);
        _hub.Reconnecting += _ => Connected(false);
        _hub.Closed += _ => Connected(false);

        await _hub.StartAsync();
        IsConnected = true;

        // Prime the table: without this a tag that is not changing stays blank until it does.
        Apply(await _hub.InvokeAsync<List<TagSample>>("GetSnapshot"));
        await _onChanged();
    }

    private Task Connected(bool connected)
    {
        IsConnected = connected;
        return _onChanged();
    }

    private void Apply(List<TagSample> samples)
    {
        foreach (var sample in samples)
            _values[sample.TagId] = sample;
        UpdateCount++;
        _dirty = true;   // repaint on the next render tick
    }

    // --- shared formatting ---------------------------------------------------
    // Every page that shows a live value formats it the same way, quality included. These were
    // private to the Live Data page; a second page copying them is how two screens start disagreeing
    // about what "bad" looks like.

    /// <summary>
    /// A value that changed within the last 1.5 s gets the highlight class; the next render drops the
    /// class and the CSS fades it out.
    /// </summary>
    public static bool IsFresh(DateTimeOffset lastChanged) =>
        DateTimeOffset.UtcNow - lastChanged < TimeSpan.FromSeconds(1.5);

    public static string QualityLed(Quality q) => q switch
    {
        Quality.Good => "led-good",
        Quality.Uncertain => "led-warn",
        _ => "led-bad",
    };

    public static string QualityCss(Quality q) => q switch
    {
        Quality.Good => "var(--hino-good)",
        Quality.Uncertain => "var(--hino-warn)",
        _ => "var(--hino-bad)",
    };

    /// <summary>Local time (the operator's), not UTC. Date only once it is not today.</summary>
    public static string FormatLastChanged(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm:ss.fff")
            : local.ToString("dd/MM HH:mm:ss");
    }

    /// <summary>
    /// Values arrive over SignalR as boxed <see cref="JsonElement"/>s, so the JsonElement branch is
    /// not a fallback — it is the normal path.
    /// </summary>
    public static string FormatValue(object? value)
    {
        if (value is null) return "—";
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.Number => je.GetDouble().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.String => je.GetString() ?? "—",
                JsonValueKind.Null or JsonValueKind.Undefined => "—",
                _ => je.ToString(),
            };
        }
        return value switch
        {
            double d => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => value.ToString() ?? "—",
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderTimer is not null) await _renderTimer.DisposeAsync();
        if (_hub is not null) await _hub.DisposeAsync();
    }
}
