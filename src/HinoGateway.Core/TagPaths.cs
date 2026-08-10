namespace HinoGateway.Core;

/// <summary>
/// Turns the tag folder tree into the runtime tag ids used everywhere downstream.
///
/// Stored tags carry a stable key plus a folder reference; the id the engine, SignalR, alarms and
/// the UI all speak is the tag's <em>full path</em> ("Area 1/Line 2/Temp"), derived here. Keeping
/// the derivation in one place is what lets a folder be renamed without touching a single stored
/// tag, alarm or screen.
/// </summary>
public static class TagPaths
{
    public const char Separator = '/';

    /// <summary>Folder id → full path. Cycle-safe: a folder whose parent chain loops (bad data in a
    /// hand-edited project file) is treated as a root rather than hanging the caller.</summary>
    public static Dictionary<string, string> BuildGroupPaths(IReadOnlyCollection<TagGroupDto> groups)
    {
        var byId = new Dictionary<string, TagGroupDto>();
        foreach (var group in groups)
            byId[group.Id] = group;

        var paths = new Dictionary<string, string>();

        foreach (var group in groups)
        {
            if (paths.ContainsKey(group.Id))
                continue;

            // Walk up to the root, collecting the chain, guarding against cycles.
            var chain = new List<TagGroupDto>();
            var visited = new HashSet<string>();
            var current = group;
            while (current is not null && visited.Add(current.Id) && !paths.ContainsKey(current.Id))
            {
                chain.Add(current);
                current = current.ParentId is not null && byId.TryGetValue(current.ParentId, out var p) ? p : null;
            }

            // 'current' is either null (reached a root), already resolved, or a cycle re-entry.
            var prefix = current is not null && paths.TryGetValue(current.Id, out var known) ? known : null;
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                prefix = prefix is null ? chain[i].Name : $"{prefix}{Separator}{chain[i].Name}";
                paths[chain[i].Id] = prefix;
            }
        }

        return paths;
    }

    /// <summary>The runtime tag id: folder path + separator + name (or just the name at root).</summary>
    public static string TagPath(IReadOnlyDictionary<string, string> groupPaths, string? groupId, string name) =>
        groupId is not null && groupPaths.TryGetValue(groupId, out var p) ? $"{p}{Separator}{name}" : name;
}
