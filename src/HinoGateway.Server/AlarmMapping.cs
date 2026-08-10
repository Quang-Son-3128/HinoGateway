using HinoGateway.Core;

namespace HinoGateway.Server;

/// <summary>
/// Maps stored <see cref="AlarmDto"/> rows to the domain <see cref="AlarmDefinition"/> the engine
/// evaluates. The one interesting step is the tag reference: alarms are stored against a tag's
/// <em>storage key</em> but evaluated against its <em>runtime path</em>, so this is where the
/// current path is looked up (and why a rename only needs a reload, not an alarm edit).
/// </summary>
public static class AlarmMapping
{
    public static List<AlarmDefinition> ToDefinitions(
        IEnumerable<AlarmDto> alarms, IReadOnlyList<TagDto> tags, ILogger? logger = null)
    {
        var pathsByTagId = tags.ToDictionary(t => t.Id, t => t.Path);
        var definitions = new List<AlarmDefinition>();

        foreach (var a in alarms)
        {
            if (!pathsByTagId.TryGetValue(a.TagId, out var path) || string.IsNullOrWhiteSpace(path))
            {
                // The alarm's tag is gone (or unresolvable): keep the configuration row, but do not
                // load a rule that could never evaluate.
                logger?.LogWarning("Alarm '{Alarm}' references unknown tag '{TagId}' — not loaded", a.Name, a.TagId);
                continue;
            }

            definitions.Add(new AlarmDefinition
            {
                Id = a.Id,
                Name = a.Name,
                TagId = path,
                Condition = a.Condition,
                Limit = a.Limit,
                Hysteresis = a.Hysteresis,
                OnDelayMs = a.OnDelayMs,
                OffDelayMs = a.OffDelayMs,
                Severity = a.Severity,
                Message = a.Message,
                Enabled = a.Enabled,
                RequiresAck = a.RequiresAck,
                TriggerOnTrue = a.TriggerOnTrue,
            });
        }

        return definitions;
    }
}
