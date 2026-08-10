using HinoGateway.Core;
using HinoGateway.Engine;
using Microsoft.AspNetCore.SignalR;

namespace HinoGateway.Server.Hubs;

/// <summary>
/// Realtime channel to the browser. The server pushes <c>TagsUpdated</c> batches; on connect a
/// client can pull the current snapshot so it renders immediately without waiting for the next poll.
/// </summary>
public sealed class TelemetryHub : Hub
{
    private readonly TagStore _store;

    public TelemetryHub(TagStore store) => _store = store;

    /// <summary>Called by a freshly connected client to seed its view with current values.</summary>
    public IReadOnlyList<TagSample> GetSnapshot()
    {
        return _store.Snapshot()
            .Select(kv => new TagSample(kv.Key, kv.Value.Value, kv.Value.Quality, kv.Value.SourceTimestamp, kv.Value.LastChanged, kv.Value.StatusText))
            .ToList();
    }
}
