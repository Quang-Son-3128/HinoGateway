using System.Net.Http.Json;
using MudBlazor;


namespace HinoGateway.Web.Services;

/// <summary>
/// Saving now applies. There used to be a second button — Save, then "Apply to engine" — and every
/// edit that skipped the second step looked done on screen while the gateway kept polling the old
/// configuration. The step was pure ceremony to everyone except the person who forgot it.
///
/// <para>
/// It is not free, which is why this is a service and not a line at the end of a save: a reload
/// rebuilds the whole runtime — every worker stopped, every driver disposed, every link reconnected.
/// One save is worth that; ten saves in a row are not. Calls inside a short window are therefore
/// coalesced into a single reload, so editing eight tags costs one interruption instead of eight.
/// </para>
///
/// <para>
/// Deliberately not used by the IEC-104 and MQTT pages. Applying there drops the link to the
/// utility's control centre, which has to stay a decision somebody makes on purpose.
/// </para>
/// </summary>
public sealed class EngineApplyCoordinator
{
    /// <summary>
    /// Long enough to swallow a burst of edits, short enough that an operator who saves and then
    /// looks at the Live Data page sees the new value there.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(400);

    private readonly HttpClient _http;
    private readonly ISnackbar _snackbar;
    private CancellationTokenSource? _pending;

    public EngineApplyCoordinator(HttpClient http, ISnackbar snackbar)
    {
        _http = http;
        _snackbar = snackbar;
    }

    /// <summary>
    /// Apply the stored configuration, unless another call arrives first — then that one applies for
    /// both. Returns when the reload this call is responsible for has finished (or been taken over),
    /// and never throws: a failed apply is reported, not propagated into a page's save path.
    /// </summary>
    public async Task RequestApplyAsync()
    {
        var mine = new CancellationTokenSource();
        var superseded = _pending;
        _pending = mine;
        superseded?.Cancel();

        try
        {
            await Task.Delay(Window, mine.Token);
        }
        catch (OperationCanceledException)
        {
            return;   // a later save took over; it will apply what this one wrote too
        }
        finally
        {
            if (ReferenceEquals(_pending, mine)) _pending = null;
            mine.Dispose();
        }

        await ApplyNowAsync();
    }

    /// <summary>Apply immediately, without waiting for the coalescing window.</summary>
    public async Task ApplyNowAsync()
    {
        try
        {
            var response = await _http.PostAsync("api/config/reload", null);
            if (!response.IsSuccessStatusCode)
            {
                _snackbar.Add($"Saved, but not applied: {await ApiResponseErrors.ReadAsync(response)}",
                    Severity.Error);
                return;
            }
            Report(_snackbar, await response.Content.ReadFromJsonAsync<ApplyResult>());
        }
        catch (Exception ex)
        {
            // The gateway going away mid-apply is a connection problem, and the offline banner is
            // already saying so; what this adds is that the edit is saved but not live.
            _snackbar.Add($"Saved, but not applied: {ex.Message}", Severity.Error);
        }
    }

    /// <summary>
    /// What the engine did with the configuration. Reconstructed 2026-08-10; mirrors
    /// <c>EngineApplyResult</c> on the server.
    /// </summary>
    public sealed class ApplyResult
    {
        public int Started { get; set; }
        public List<RejectedConnection> Rejected { get; set; } = new();
        public List<DroppedTag> DroppedTags { get; set; } = new();

        public sealed class RejectedConnection
        {
            public string DeviceId { get; set; } = "";
            public string? LastError { get; set; }
        }

        public sealed class DroppedTag
        {
            public string DeviceId { get; set; } = "";
            public string TagId { get; set; } = "";
            public string Reason { get; set; } = "";
        }
    }

    /// <summary>
    /// Say what actually happened, not "Applied".
    ///
    /// <para>
    /// An apply can succeed as an HTTP call while refusing a connection or dropping a tag, and an
    /// unconditional green message taught operators to ignore the one screen that would have told
    /// them a tag stopped being read.
    /// </para>
    /// </summary>
    public static void Report(ISnackbar snackbar, ApplyResult? result)
    {
        if (result is null)
        {
            snackbar.Add("Saved and applied.", Severity.Success);
            return;
        }

        foreach (var rejected in result.Rejected)
            snackbar.Add($"Connection '{rejected.DeviceId}' is not running: {rejected.LastError}", Severity.Error);
        foreach (var dropped in result.DroppedTags)
            snackbar.Add($"Tag '{dropped.TagId}' was dropped: {dropped.Reason}", Severity.Warning);

        if (result.Rejected.Count == 0 && result.DroppedTags.Count == 0)
            snackbar.Add($"Saved and applied — {result.Started} connection(s) running.", Severity.Success);
    }
}
