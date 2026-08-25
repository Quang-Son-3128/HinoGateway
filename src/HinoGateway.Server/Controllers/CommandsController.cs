using HinoGateway.Core;
using HinoGateway.Expressions;
using HinoGateway.Server.Security;
using HinoGateway.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HinoGateway.Server.Controllers;

/// <summary>
/// Commands: the scripts that turn a written tag into writes to the plant. Admin only — a command is
/// a standing authorisation to move a setpoint, which is more than any single write.
/// </summary>
[ApiController]
[Route("api/commands")]
[Authorize(Policy = GatewayPolicies.Admin)]
public sealed class CommandsController : ControllerBase
{
    private readonly IConfigStore _store;
    private readonly CommandEngine _commands;

    public CommandsController(IConfigStore store, CommandEngine commands)
    {
        _store = store;
        _commands = commands;
    }

    /// <summary>Every command, with what it reads, what it writes and how its last run went.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<CommandView>> Get(CancellationToken ct)
    {
        var commands = await _store.GetCommandsAsync(ct);
        return commands.Select(c => Describe(c, _commands.LastRun(c.Id))).ToList();
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Upsert(string id, [FromBody] CommandDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Id) || dto.Id.Trim() != id)
            return BadRequest("Command id in body must match the URL.");
        dto.Name = dto.Name?.Trim() ?? "";

        var tags = await _store.GetTagsAsync(ct);
        var groups = await _store.GetGroupsAsync(ct);
        var problems = ProjectValidator.ValidateCommands(new[] { dto }, tags, groups);
        if (problems.Count > 0)
            return BadRequest(problems);

        await _store.UpsertCommandAsync(dto, ct);
        // Commands take effect immediately. There is no engine to rebuild — a command is a script
        // over the live tag store — so making the operator press Apply as well would be ceremony.
        await _commands.ReloadAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (!await _store.DeleteCommandAsync(id, ct))
            return NotFound();
        await _commands.ReloadAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Run a command against the live values without writing anything, for a given trigger value.
    ///
    /// <para>
    /// This is the whole reason writes are collected rather than performed inside the script: the dry
    /// run is the same code path as the real one, so what it lists is what would actually be written.
    /// Commissioning a curtailment command without it means asking a control centre to send test
    /// orders at a live plant.
    /// </para>
    /// </summary>
    [HttpPost("{id}/test")]
    public async Task<ActionResult<CommandTestResult>> Test(string id, [FromBody] CommandTestRequest request,
        [FromServices] HinoGateway.Engine.DeviceEngine engine, CancellationToken ct)
    {
        var command = (await _store.GetCommandsAsync(ct)).FirstOrDefault(c => c.Id == id);
        if (command is null) return NotFound();

        if (!ScriptCompiler.TryCompile(command.Script, out var compiled, out var error, out var position))
            return BadRequest($"{error} (at character {position + 1}).");

        var value = ParseTriggerValue(request.Value);
        var run = compiled!.Run(engine.Tags, value, DateTimeOffset.Now);

        return new CommandTestResult(
            run.Completed,
            run.Writes.Select(w => new CommandTestWrite(w.TagPath, CommandEngine.Describe(w.Value))).ToList(),
            run.Error);
    }

    /// <summary>
    /// Check a script without saving it — the editor's as-you-type check, which sees a half-written
    /// command on nearly every keystroke.
    /// </summary>
    [HttpPost("check")]
    public async Task<ActionResult<CommandCheckResult>> Check([FromBody] CommandDto dto, CancellationToken ct)
    {
        if (!ScriptCompiler.TryCompile(dto.Script ?? "", out var compiled, out var error, out var position))
            return new CommandCheckResult(false, error, position, Array.Empty<string>(), Array.Empty<string>());

        var tags = await _store.GetTagsAsync(ct);
        var groups = await _store.GetGroupsAsync(ct);
        var problems = ProjectValidator.ValidateCommands(new[] { dto }, tags, groups);

        return new CommandCheckResult(problems.Count == 0, problems.Count == 0 ? null : string.Join(" ", problems), 0,
            compiled!.ReadTags.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            compiled.WrittenTags.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    private static CommandView Describe(CommandDto command, CommandRunInfo? lastRun)
    {
        var reads = Array.Empty<string>();
        var writes = Array.Empty<string>();
        if (ScriptCompiler.TryCompile(command.Script ?? "", out var compiled, out _, out _))
        {
            reads = compiled!.ReadTags.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            writes = compiled.WrittenTags.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
        return new CommandView(command, reads, writes, lastRun);
    }

    /// <summary>
    /// The trigger value a test runs with. Text in, because a command can be triggered by a bool tag
    /// as well as a number, and "false" and 0 are different things to a script.
    /// </summary>
    private static ExpressionValue ParseTriggerValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ExpressionValue.Good(0d);
        if (bool.TryParse(text, out var flag)) return ExpressionValue.Good(flag);
        if (double.TryParse(text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return ExpressionValue.Good(number);
        return ExpressionValue.Good(text);
    }
}

/// <summary>A command plus everything derived from its script, for the screen.</summary>
public sealed record CommandView(CommandDto Command, IReadOnlyList<string> Reads,
    IReadOnlyList<string> Writes, CommandRunInfo? LastRun);

public sealed record CommandTestRequest(string? Value);

public sealed record CommandTestResult(bool Completed, IReadOnlyList<CommandTestWrite> Writes, string? Error);

public sealed record CommandTestWrite(string TagPath, string? Value);

public sealed record CommandCheckResult(bool Ok, string? Error, int Position,
    IReadOnlyList<string> Reads, IReadOnlyList<string> Writes);
