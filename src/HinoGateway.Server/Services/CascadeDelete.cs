using HinoGateway.Core;
using HinoGateway.Expressions;

namespace HinoGateway.Server.Services;

/// <summary>
/// What deleting a set of tags takes with it. Reported before the fact (so the operator can confirm)
/// and after it (so the caller can say what happened) using the same shape.
/// </summary>
/// <param name="Tags">Tags removed.</param>
/// <param name="Alarms">Alarm rules removed because the tag they watch is gone.</param>
/// <param name="Iec104Points">IEC-104 points removed because the tag they map is gone.</param>
/// <param name="Accumulators">
/// Accumulator tags removed because the counter they add up is gone. Counted in <see cref="Tags"/> too.
/// </param>
/// <param name="BrokenFormulas">
/// Runtime paths of expression tags that read one of the deleted tags. These are <em>not</em> deleted:
/// a formula is the operator's own work and may read ten tags, only one of which is going. They are
/// reported so the confirmation can say so, because until they are fixed the project will not apply.
/// </param>
/// <param name="BrokenCommands">
/// Names of commands whose trigger tag, or a tag their script reads or writes, is being deleted.
/// Not deleted either, and for the same reason — a command is a piece of plant logic somebody wrote.
/// </param>
public sealed record CascadeResult(
    int Tags,
    int Alarms,
    int Iec104Points,
    int Accumulators,
    IReadOnlyList<string> BrokenFormulas,
    IReadOnlyList<string> BrokenCommands)
{
    public static readonly CascadeResult Nothing =
        new(0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Deleting a connection, a folder or a tag used to be refused while anything still pointed at it,
/// which left the operator to remove two hundred tags by hand. Now the dependants go with it, in one
/// transaction, after a confirmation that says exactly how much.
///
/// Doing this properly matters beyond convenience: the old <c>DeleteTag</c> checked alarms but not
/// IEC-104 points, so deleting a mapped tag succeeded silently and then every apply failed with
/// "IOA 102 references a tag that does not exist".
/// </summary>
public static class CascadeDelete
{
    /// <summary>What would go, without changing anything.</summary>
    public static async Task<CascadeResult> PreviewAsync(IConfigStore store, IAlarmStore alarms,
        IReadOnlyCollection<string> tagIds, CancellationToken ct)
    {
        if (tagIds.Count == 0) return CascadeResult.Nothing;
        var plan = await PlanAsync(store, alarms, tagIds, ct);
        return plan.ToResult();
    }

    /// <summary>
    /// Remove the tags and everything that only existed because of them, as a single write. The caller
    /// is expected to already be inside a transaction when it has more to delete (a device, a folder).
    /// </summary>
    public static async Task<CascadeResult> RemoveTagsAsync(IConfigStore store, IAlarmStore alarms,
        IReadOnlyCollection<string> tagIds, CancellationToken ct)
    {
        if (tagIds.Count == 0) return CascadeResult.Nothing;

        var plan = await PlanAsync(store, alarms, tagIds, ct);

        foreach (var alarmId in plan.AlarmIds)
            await alarms.DeleteAlarmAsync(alarmId, ct);

        if (plan.Iec104Points > 0)
        {
            var iec = await store.GetIec104Async(ct);
            iec.Points.RemoveAll(p => plan.Doomed.Contains(p.TagId));
            await store.SaveIec104Async(iec, ct);
        }

        var removed = 0;
        foreach (var tagId in plan.Doomed)
            if (await store.DeleteTagAsync(tagId, ct))
                removed++;

        return plan.ToResult() with { Tags = removed };
    }

    private static async Task<Plan> PlanAsync(IConfigStore store, IAlarmStore alarms,
        IReadOnlyCollection<string> tagIds, CancellationToken ct)
    {
        var tags = await store.GetTagsAsync(ct);
        var groups = await store.GetGroupsAsync(ct);
        var doomed = tags.Where(t => tagIds.Contains(t.Id)).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        // An accumulator is nothing but a running total of one counter; without that counter it can
        // neither be validated nor computed, so it goes too. It cannot count another accumulator
        // (ProjectValidator forbids it), so one pass is enough — there is no fixpoint to chase.
        var accumulators = tags
            .Where(t => t.Kind == TagKind.Accumulator
                        && !string.IsNullOrWhiteSpace(t.SourceTagKey)
                        && doomed.Contains(t.SourceTagKey!)
                        && !doomed.Contains(t.Id))
            .Select(t => t.Id)
            .ToList();
        foreach (var id in accumulators)
            doomed.Add(id);

        var alarmIds = (await alarms.GetAlarmsAsync(ct))
            .Where(a => doomed.Contains(a.TagId))
            .Select(a => a.Id)
            .ToList();

        var iec = await store.GetIec104Async(ct);
        var points = iec.Points.Count(p => doomed.Contains(p.TagId));

        return new Plan(doomed, alarmIds, points, accumulators.Count,
            BrokenFormulas(tags, groups, doomed),
            await BrokenCommandsAsync(store, tags, groups, doomed, ct));
    }

    /// <summary>
    /// Expression tags whose formula reads one of the doomed tags, by runtime path. Formulas name
    /// tags by path, so the doomed storage ids are turned into paths first.
    /// </summary>
    private static IReadOnlyList<string> BrokenFormulas(IReadOnlyList<TagDto> tags,
        IReadOnlyList<TagGroupDto> groups, IReadOnlySet<string> doomed)
    {
        var paths = TagPaths.BuildGroupPaths(groups);
        var doomedPaths = tags
            .Where(t => doomed.Contains(t.Id) && !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => TagPaths.TagPath(paths, t.GroupId, t.Name!.Trim()))
            .ToHashSet(StringComparer.Ordinal);
        if (doomedPaths.Count == 0) return Array.Empty<string>();

        var broken = new List<string>();
        foreach (var tag in tags.Where(t => t.Kind == TagKind.Expression
                                            && !doomed.Contains(t.Id)
                                            && !string.IsNullOrWhiteSpace(t.Expression)
                                            && !string.IsNullOrWhiteSpace(t.Name)))
        {
            // A formula that does not compile is somebody else's problem; it cannot be read for
            // references and the validator already reports it.
            if (!ExpressionCompiler.TryCompile(tag.Expression!, out var compiled, out _, out _))
                continue;
            if (compiled!.ReferencedTags.Any(doomedPaths.Contains))
                broken.Add(TagPaths.TagPath(paths, tag.GroupId, tag.Name!.Trim()));
        }
        return broken.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Commands left pointing at a tag that is going: the trigger they wait on, or a tag their script
    /// reads or writes. Reported, never deleted - a command is plant logic somebody wrote, and the
    /// project refuses to apply until it is corrected, so silence here would be the worst outcome.
    /// </summary>
    private static async Task<IReadOnlyList<string>> BrokenCommandsAsync(IConfigStore store,
        IReadOnlyList<TagDto> tags, IReadOnlyList<TagGroupDto> groups, IReadOnlySet<string> doomed,
        CancellationToken ct)
    {
        var commands = await store.GetCommandsAsync(ct);
        if (commands.Count == 0) return Array.Empty<string>();

        var paths = TagPaths.BuildGroupPaths(groups);
        var doomedPaths = tags
            .Where(t => doomed.Contains(t.Id) && !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => TagPaths.TagPath(paths, t.GroupId, t.Name!.Trim()))
            .ToHashSet(StringComparer.Ordinal);

        var broken = new List<string>();
        foreach (var command in commands)
        {
            var hit = doomed.Contains(command.TriggerTagId);
            if (!hit && ScriptCompiler.TryCompile(command.Script ?? "", out var script, out _, out _))
                hit = script!.ReadTags.Any(doomedPaths.Contains) || script.WrittenTags.Any(doomedPaths.Contains);
            if (hit)
                broken.Add(string.IsNullOrWhiteSpace(command.Name) ? command.Id : command.Name);
        }
        return broken.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private sealed record Plan(
        HashSet<string> Doomed,
        IReadOnlyList<string> AlarmIds,
        int Iec104Points,
        int Accumulators,
        IReadOnlyList<string> BrokenFormulas,
        IReadOnlyList<string> BrokenCommands)
    {
        public CascadeResult ToResult() =>
            new(Doomed.Count, AlarmIds.Count, Iec104Points, Accumulators, BrokenFormulas, BrokenCommands);
    }
}
