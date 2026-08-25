namespace HinoGateway.Core;

/// <summary>
/// A command: what the gateway does when somebody writes to a particular tag.
///
/// <para>
/// This is the piece that was missing between the utility's control centre and the plant. An IEC-104
/// point maps to a tag, and for a setpoint the control centre expects that number to reach the
/// equipment — but the tag it maps is usually a memory tag, deliberately, because the control centre
/// speaks in percentages of the plant while the PLC wants a kilowatt figure in a specific register.
/// Without something in between, the number arrived and stopped: it sat in the gateway's memory,
/// general interrogation read it back correctly, and nothing happened in the field.
/// </para>
///
/// <para>
/// Triggering on a <b>tag write</b> rather than on an IEC address is what keeps this out of the
/// protocol layer. Select-before-operate, activation confirmation and termination, the command queue
/// and the audit trail all stay exactly as they are; a command is one more thing that happens when a
/// tag changes. It also means the same command can be exercised from the UI or the API, which is the
/// only way to test one without booking time with a control centre.
/// </para>
/// </summary>
public sealed class CommandDto
{
    /// <summary>Stable storage key (GUID for new commands).</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Off means the script is kept but never runs — how a command is parked, not deleted.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Storage key of the tag whose value starts this command — never a path. A path is a position
    /// in the folder tree, and one rename would silently disconnect the plant's curtailment control
    /// from the control centre.
    /// </summary>
    public string TriggerTagId { get; set; } = "";

    /// <summary>The trigger's runtime path, computed server-side. Read-only for clients.</summary>
    public string TriggerTagPath { get; set; } = "";

    /// <summary>
    /// The script, in the gateway's own statement language: <c>var</c>, <c>if</c>/<c>else</c>,
    /// <c>return</c>, <c>Tag("path")</c> and <c>Write("path", value)</c>, with <c>value</c> holding
    /// what was just written into the trigger tag. No loops — see <c>ScriptParser</c>.
    /// </summary>
    public string Script { get; set; } = "";
}

/// <summary>What happened the last time a command ran. Kept in memory; lost on restart, by design.</summary>
/// <remarks>
/// This is the answer to the question that is otherwise unanswerable without reading logs on the
/// industrial PC: "the control centre says it sent a setpoint — did the gateway do anything?".
/// </remarks>
public sealed record CommandRunInfo(
    string CommandId,
    DateTimeOffset At,
    string? TriggerValue,
    IReadOnlyList<CommandWriteInfo> Writes,
    bool Succeeded,
    string? Error);

/// <summary>One tag a command wrote, and how that write went.</summary>
public sealed record CommandWriteInfo(string TagPath, string? Value, bool Succeeded, string? Error);
