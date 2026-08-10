using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Expressions;
using HinoGateway.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HinoGateway.Server.Controllers;

/// <summary>
/// Support for the formula editor: check an expression as it is typed, try it against the live tag
/// values, and list the functions that exist.
///
/// <para>
/// The point of checking here rather than in the browser is that this is the same compiler the engine
/// runs. A second implementation in the client would eventually disagree with it, and the disagreement
/// would show up as a formula the editor accepted and the gateway refused.
/// </para>
/// </summary>
[ApiController]
[Route("api/expressions")]
[Authorize(Policy = GatewayPolicies.Viewer)]
public sealed class ExpressionsController : ControllerBase
{
    private readonly IConfigStore _store;

    public ExpressionsController(IConfigStore store) => _store = store;

    /// <summary>
    /// Compile a formula and report what it needs. <paramref name="request"/> carries the text; the
    /// answer says whether it is valid, where the error is, which tags it reads and which of those do
    /// not exist — everything the editor needs to explain itself before anything is saved.
    /// </summary>
    [HttpPost("validate")]
    public async Task<ExpressionCheck> Validate([FromBody] ExpressionRequest request, CancellationToken ct)
    {
        if (!ExpressionCompiler.TryCompile(request.Expression ?? "", out var compiled, out var error, out var position))
            return new ExpressionCheck { Valid = false, Error = error, Position = position };

        var known = (await _store.GetTagsAsync(ct)).Select(t => t.Path).ToHashSet(StringComparer.Ordinal);
        var referenced = compiled!.ReferencedTags.OrderBy(t => t, StringComparer.Ordinal).ToList();
        return new ExpressionCheck
        {
            Valid = true,
            ReferencedTags = referenced,
            UnknownTags = referenced.Where(t => !known.Contains(t)).ToList(),
        };
    }

    /// <summary>
    /// Evaluate a formula once against the values the gateway holds right now — the editor's "try it"
    /// button. Read-only: nothing is stored, no tag is written, and the result carries the same
    /// quality an expression tag would have produced.
    /// </summary>
    [HttpPost("evaluate")]
    public ActionResult<ExpressionResult> Evaluate(
        [FromBody] ExpressionRequest request, [FromServices] DeviceEngine engine)
    {
        CompiledExpression compiled;
        try
        {
            compiled = ExpressionCompiler.Compile(request.Expression ?? "");
        }
        catch (ExpressionSyntaxException ex)
        {
            return BadRequest(new ExpressionCheck { Valid = false, Error = ex.Message, Position = ex.Position });
        }

        var value = compiled.Evaluate(new EvaluationContext(engine.Tags, DateTimeOffset.Now, compiled.CreateMemory()));
        return new ExpressionResult
        {
            // prev() and dt() have no history in a one-off evaluation and report Bad; that is the
            // honest answer here rather than a number invented for the preview.
            Value = value.Value,
            Quality = value.Quality,
            Status = value.Status,
            Text = value.AsText(),
        };
    }

    /// <summary>The built-in functions, for the editor's help list.</summary>
    [HttpGet("functions")]
    public IReadOnlyList<ExpressionFunctionInfo> GetFunctions() =>
        Functions.All
            .Select(f => new ExpressionFunctionInfo
            {
                Name = f.Name,
                Category = f.Category,
                Signature = f.Signature,
                Help = f.Help,
            })
            .ToList();
}

public sealed class ExpressionRequest
{
    public string? Expression { get; set; }
}

public sealed class ExpressionCheck
{
    public bool Valid { get; set; }

    /// <summary>Null when valid.</summary>
    public string? Error { get; set; }

    /// <summary>0-based character position of the error, so the editor can point at it.</summary>
    public int Position { get; set; }

    public IReadOnlyList<string> ReferencedTags { get; set; } = Array.Empty<string>();

    /// <summary>Referenced tags that are not in the tag database — a rename left behind, usually.</summary>
    public IReadOnlyList<string> UnknownTags { get; set; } = Array.Empty<string>();
}

public sealed class ExpressionResult
{
    public object? Value { get; set; }
    public Quality Quality { get; set; }
    public string? Status { get; set; }
    public string Text { get; set; } = "";
}

public sealed class ExpressionFunctionInfo
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Help { get; set; } = "";
}
