using System.Net.Http.Json;

namespace HinoGateway.Web.Services;

/// <summary>
/// What a delete would take with it, as the server counted it. Mirrors <c>CascadeResult</c> on the
/// server side.
///
/// <para>
/// The counts exist so the confirmation dialog can name them. A cascade the operator cannot see
/// coming is worse than the refusal it replaced: "Delete PLC_Riken?" and "Delete PLC_Riken, its 40
/// tags, 3 alarm rules and 21 IEC-104 points?" are not the same question.
/// </para>
/// </summary>
public sealed record CascadeImpact(
    int Tags,
    int Alarms,
    int Iec104Points,
    int Accumulators,
    IReadOnlyList<string> BrokenFormulas,
    IReadOnlyList<string> BrokenCommands)
{
    public static readonly CascadeImpact Unknown =
        new(-1, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// Never throws: a delete confirmation must still appear when the count cannot be fetched. The
    /// dialog then asks the plain question instead of a quantified one.
    /// </summary>
    public static async Task<CascadeImpact> FetchAsync(HttpClient http, string url)
    {
        try
        {
            return await http.GetFromJsonAsync<CascadeImpact>(url) ?? Unknown;
        }
        catch
        {
            return Unknown;
        }
    }

    /// <param name="self">
    /// How many of <see cref="Tags"/> the operator already knows about because it is the thing being
    /// deleted. Deleting one tag reports "1 tag" otherwise, which reads like a second one.
    /// </param>
    public string Describe(int self = 0)
    {
        if (Tags < 0) return "This also deletes everything that points at it.";

        var parts = new List<string>();
        var others = Tags - self;
        if (others > 0) parts.Add($"{others} tag(s)");
        if (Alarms > 0) parts.Add($"{Alarms} alarm rule(s)");
        if (Iec104Points > 0) parts.Add($"{Iec104Points} IEC-104 point(s)");

        var sentence = parts.Count == 0
            ? "Nothing else points at it."
            : $"This also deletes {string.Join(", ", parts)}.";

        if (Accumulators > 0)
            sentence += $" {Accumulators} of the tags are accumulated totals of a counter being deleted.";

        // Not deleted, and the reason it has to be said out loud: the project will not apply until
        // those formulas are fixed, and that failure would otherwise surface on an unrelated save.
        if (BrokenFormulas.Count > 0)
            sentence += $" {BrokenFormulas.Count} formula(s) read a tag being deleted and will have to be "
                        + $"fixed before anything can be applied: {string.Join(", ", BrokenFormulas.Take(5))}"
                        + (BrokenFormulas.Count > 5 ? ", …" : "") + ".";

        if (BrokenCommands.Count > 0)
            sentence += $" {BrokenCommands.Count} command(s) will stop working until they are fixed: "
                        + string.Join(", ", BrokenCommands.Take(5))
                        + (BrokenCommands.Count > 5 ? ", …" : "") + ".";

        return sentence;
    }
}
