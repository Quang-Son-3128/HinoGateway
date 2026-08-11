using HinoGateway.Core;

namespace HinoGateway.Engine;

/// <summary>
/// Coalesces polling batches into the latest unsent value for each tag and suppresses unchanged
/// values. The buffer deliberately separates <see cref="Drain"/> from <see cref="MarkSent"/> so a
/// failed northbound send is retried when the tag is sampled again instead of being silently lost.
/// </summary>
public sealed class TelemetryDeltaBuffer
{
    private readonly Dictionary<string, TagSample> _lastSent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TagSample> _pending = new(StringComparer.Ordinal);

    public void Add(IReadOnlyList<TagRead> batch)
    {
        foreach (var read in batch)
        {
            var sample = new TagSample(read.TagId, read.Value.Value, read.Value.Quality,
                read.Value.SourceTimestamp, read.Value.LastChanged, read.Value.StatusText);
            if (!_lastSent.TryGetValue(read.TagId, out var previous) || Changed(previous, sample))
                _pending[read.TagId] = sample;
            else
                _pending.Remove(read.TagId);
        }
    }

    public IReadOnlyList<TagSample> Drain()
    {
        if (_pending.Count == 0)
            return Array.Empty<TagSample>();

        // A tag may change away from and back to its last transmitted state during one window.
        var result = _pending.Values
            .Where(sample => !_lastSent.TryGetValue(sample.TagId, out var previous)
                             || Changed(previous, sample))
            .ToList();
        _pending.Clear();
        return result;
    }

    public void MarkSent(IEnumerable<TagSample> samples)
    {
        foreach (var sample in samples)
            _lastSent[sample.TagId] = sample;
    }

    /// <summary>Forget tags removed by a configuration reload.</summary>
    public void RetainOnly(IReadOnlySet<string> tagIds)
    {
        foreach (var key in _lastSent.Keys.Where(key => !tagIds.Contains(key)).ToList())
            _lastSent.Remove(key);
        foreach (var key in _pending.Keys.Where(key => !tagIds.Contains(key)).ToList())
            _pending.Remove(key);
    }

    private static bool Changed(TagSample previous, TagSample current) =>
        previous.Quality != current.Quality
        || !Equals(previous.Value, current.Value)
        || !string.Equals(previous.StatusText, current.StatusText, StringComparison.Ordinal);
}
