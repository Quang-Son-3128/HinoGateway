namespace HinoGateway.Core;

/// <summary>
/// Where the values of <see cref="TagDefinition.Retentive"/> memory tags survive a restart.
///
/// <para>
/// Deliberately <em>not</em> the project file. A project file describes what the gateway is
/// configured to do and is edited, exported, versioned and copied between machines; a retained value
/// is operational state that changes whenever an operator turns a dial. Writing one into the other
/// would mean rewriting the whole configuration on every setpoint change — a large file rewritten
/// often, on an industrial PC that can lose power at any moment, for data nobody wants in an export.
/// A separate sidecar keeps the two lifetimes apart.
/// </para>
///
/// <para>
/// Implementations must be safe to call from any thread and must never block the caller on disk:
/// a write comes from an operator's request and from the tag engine, neither of which can wait for a
/// flush. Losing the last second of writes to a power cut is acceptable here; stalling the gateway is
/// not.
/// </para>
/// </summary>
public interface IRetainedValueStore
{
    /// <summary>The value kept for <paramref name="tagId"/>, if there is one.</summary>
    bool TryGet(string tagId, out object? value);

    /// <summary>Remember a new value. May be written to disk lazily.</summary>
    void Save(string tagId, object? value);

    /// <summary>
    /// Forget everything not in <paramref name="tagIds"/> — the tags that are no longer configured as
    /// retentive. Called after a configuration is applied, so that turning the setting off (or
    /// deleting a tag) actually drops the stored value instead of leaving it to reappear years later
    /// under a re-used name.
    /// </summary>
    void RetainOnly(IEnumerable<string> tagIds);
}
