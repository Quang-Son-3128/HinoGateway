using System.Globalization;
using System.Text;
using HinoGateway.Core;
using HinoGateway.Server.Services;
using HinoGateway.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HinoGateway.Server.Controllers;

/// <summary>
/// Configuration and status of the IEC-104 controlled station (the link to the utility's control
/// centre), plus CSV import/export of the signal list — utilities hand that over as a spreadsheet,
/// and typing a few hundred IOAs by hand is how mistakes get made.
/// </summary>
[ApiController]
[Route("api/iec104")]
[Authorize(Policy = GatewayPolicies.Viewer)]
public sealed class Iec104Controller : ControllerBase
{
    /// <summary>IEC 60870-5-101/104 information object addresses are three octets.</summary>
    private readonly IConfigStore _store;
    private readonly Iec104HostedService _link;

    public Iec104Controller(IConfigStore store, Iec104HostedService link)
    {
        _store = store;
        _link = link;
    }

    [HttpGet]
    public Task<Iec104ServerOptions> Get(CancellationToken ct) => _store.GetIec104Async(ct);

    [HttpGet("status")]
    public Iec104Status GetStatus() => _link.CurrentStatus();

    [HttpPut]
    [Authorize(Policy = GatewayPolicies.Admin)]
    public async Task<IActionResult> Save([FromBody] Iec104ServerOptions options, CancellationToken ct)
    {
        var problems = await ValidateAsync(options, ct);
        if (problems.Count > 0)
            return BadRequest(string.Join(" | ", problems));

        await _store.SaveIec104Async(options, ct);
        return NoContent();
    }

    /// <summary>Restart the station from the saved configuration.</summary>
    [HttpPost("apply")]
    [Authorize(Policy = GatewayPolicies.Admin)]
    public async Task<ActionResult<Iec104Status>> Apply(CancellationToken ct) => await _link.ApplyAsync(ct);

    // --- signal list as CSV --------------------------------------------------

    private const string CsvHeader = "TagPath,Ioa,Type,Deadband,WithTimestamp,Command,ReportScale,RequireSelect";

    [HttpGet("points/csv")]
    public async Task<IActionResult> ExportCsv(CancellationToken ct)
    {
        var options = await _store.GetIec104Async(ct);
        var csv = new StringBuilder();
        // A comment line first: the file is read by people comparing it against the utility's list.
        csv.AppendLine("# Type/Command accept a number (T13), a standard name (M_ME_NC_1) or the name below.");
        csv.AppendLine(CsvHeader);
        foreach (var point in options.Points.OrderBy(p => p.Ioa))
        {
            csv.AppendLine(string.Join(',',
                Quote(point.TagPath),
                point.Ioa.ToString(CultureInfo.InvariantCulture),
                $"T{Iec104TypeIds.Number(point.Type, point.WithTimestamp)}",
                point.Deadband.ToString(CultureInfo.InvariantCulture),
                point.WithTimestamp,
                point.Command == Iec104CommandType.None ? "None" : $"T{Iec104TypeIds.Number(point.Command)}",
                point.ReportScale.ToString(CultureInfo.InvariantCulture),
                point.RequireSelect));
        }

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv",
            $"iec104-points-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
    }

    /// <summary>
    /// Replace the signal list from CSV. Tags are matched by their runtime path, which is what the
    /// utility's list will be written against; every problem is reported with its line number and
    /// nothing is saved unless the whole file is usable.
    /// </summary>
    [HttpPost("points/csv")]
    [Authorize(Policy = GatewayPolicies.Admin)]
    public async Task<ActionResult<CsvImportResult>> ImportCsv([FromBody] CsvImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Csv))
            return BadRequest("The CSV is empty.");

        var tagsByPath = (await _store.GetTagsAsync(ct))
            .ToDictionary(t => t.Path, t => t.Id, StringComparer.OrdinalIgnoreCase);

        var points = new List<Iec104PointDto>();
        var problems = new List<string>();
        var lines = request.Csv.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (i == 0 && line.StartsWith("TagPath", StringComparison.OrdinalIgnoreCase))
                continue;   // header

            var fields = SplitCsv(line);
            if (fields.Count < 3)
            {
                problems.Add($"line {i + 1}: expected at least TagPath,Ioa,Type");
                continue;
            }

            var path = fields[0];
            if (!tagsByPath.TryGetValue(path, out var tagId))
            {
                problems.Add($"line {i + 1}: no tag named '{path}'");
                continue;
            }

            if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ioa))
            {
                problems.Add($"line {i + 1}: '{fields[1]}' is not a valid IOA");
                continue;
            }

            // The utility's list is written in type identification numbers, so accept those as well
            // as our own names — retyping "T13" as "ShortFloat" for hundreds of points invites errors.
            if (!Iec104TypeIds.TryParsePointType(fields[2], out var type))
            {
                problems.Add($"line {i + 1}: unknown type '{fields[2]}' — use a number like T13, " +
                             $"a standard name like M_ME_NC_1, or one of " +
                             $"{string.Join('/', Enum.GetNames<Iec104PointType>())}");
                continue;
            }

            var point = new Iec104PointDto { TagId = tagId, TagPath = path, Ioa = ioa, Type = type };

            if (fields.Count > 3 && fields[3].Length > 0
                && double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var deadband))
                point.Deadband = deadband;

            if (fields.Count > 4 && fields[4].Length > 0 && bool.TryParse(fields[4], out var withTime))
                point.WithTimestamp = withTime;

            if (fields.Count > 5 && fields[5].Length > 0)
            {
                if (!Iec104TypeIds.TryParseCommandType(fields[5], out var command))
                {
                    problems.Add($"line {i + 1}: unknown command '{fields[5]}' — use a number like T50, " +
                                 $"a standard name like C_SE_NC_1, or one of " +
                                 $"{string.Join('/', Enum.GetNames<Iec104CommandType>())}");
                    continue;
                }
                point.Command = command;
                if (command is Iec104CommandType.SingleCommand or Iec104CommandType.DoubleCommand)
                    point.RequireSelect = true;
            }

            if (fields.Count > 6 && fields[6].Length > 0
                && double.TryParse(fields[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
                point.ReportScale = scale;

            if (fields.Count > 7 && fields[7].Length > 0 && bool.TryParse(fields[7], out var requireSelect))
                point.RequireSelect = requireSelect;

            points.Add(point);
        }

        if (problems.Count > 0)
            return BadRequest(string.Join(" | ", problems.Take(20)));

        var options = await _store.GetIec104Async(ct);
        options.Points = points;

        var validation = await ValidateAsync(options, ct);
        if (validation.Count > 0)
            return BadRequest(string.Join(" | ", validation));

        await _store.SaveIec104Async(options, ct);
        return new CsvImportResult(points.Count);
    }

    public sealed record CsvImportRequest(string Csv);

    public sealed record CsvImportResult(int Points);

    private static string Quote(string value) =>
        value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    /// <summary>Minimal CSV field splitter: handles quoted fields containing commas.</summary>
    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString().Trim());
        return fields;
    }

    // --- validation ----------------------------------------------------------

    /// <summary>
    /// The same check the engine applies when the whole project is validated. It used to be a second,
    /// near-identical copy here, which had drifted: it missed the select-before-operate rule and the
    /// AllowWrite rule, so this page accepted a mapping that a later apply refused.
    /// </summary>
    private async Task<List<string>> ValidateAsync(Iec104ServerOptions options, CancellationToken ct) =>
        ProjectValidator.ValidateIec104(options, await _store.GetTagsAsync(ct));
}
