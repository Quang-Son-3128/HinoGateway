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
    private const int MaxIoa = 16_777_215;

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

    private async Task<List<string>> ValidateAsync(Iec104ServerOptions options, CancellationToken ct)
    {
        var problems = new List<string>();

        if (options.Port is < 1 or > 65535)
            problems.Add($"Port must be between 1 and 65535 (got {options.Port}).");
        if (string.IsNullOrWhiteSpace(options.BindAddress))
            problems.Add("A bind address is required (use 0.0.0.0 for every interface).");
        if (options.CommonAddress is < 1 or > 65534)
            problems.Add($"Common address must be between 1 and 65534 (got {options.CommonAddress}).");
        if (options.MaxConnections is < 1 or > 32)
            problems.Add($"Max connections must be between 1 and 32 (got {options.MaxConnections}).");
        if (options.CommandQueueCapacity is < 1 or > 10_000)
            problems.Add("Command queue capacity must be between 1 and 10000.");
        if (options.CommandTimeoutMs is < 100 or > 600_000)
            problems.Add("Command timeout must be between 100 and 600000 ms.");
        if (options.K is < 1 or > 32767)
            problems.Add("k must be between 1 and 32767.");
        if (options.W is < 1 or > 32767)
            problems.Add("w must be between 1 and 32767.");
        if (options.W > options.K)
            problems.Add($"w ({options.W}) must not exceed k ({options.K}).");
        if (options.T2 >= options.T1)
            problems.Add($"t2 ({options.T2}s) must be shorter than t1 ({options.T1}s).");
        foreach (var (name, value) in new[] { ("t0", options.T0), ("t1", options.T1), ("t2", options.T2), ("t3", options.T3) })
            if (value is < 1 or > 255)
                problems.Add($"{name} must be between 1 and 255 seconds (got {value}).");
        if (options.PeriodicIntervalSeconds is < 0 or > 86400)
            problems.Add("The periodic interval must be between 0 (off) and 86400 seconds.");

        var tags = await _store.GetTagsAsync(ct);
        var tagsById = tags.ToDictionary(t => t.Id);

        foreach (var duplicate in options.Points.GroupBy(p => p.Ioa).Where(g => g.Count() > 1))
            problems.Add($"IOA {duplicate.Key} is mapped {duplicate.Count()} times — each address must be unique.");

        foreach (var point in options.Points)
        {
            if (point.Ioa is < 1 or > MaxIoa)
            {
                problems.Add($"IOA {point.Ioa} is out of range (1 … {MaxIoa}).");
                continue;
            }

            if (!tagsById.TryGetValue(point.TagId, out var tag))
            {
                problems.Add($"IOA {point.Ioa} references a tag that does not exist.");
                continue;
            }

            if (!Enum.IsDefined(point.Type))
                problems.Add($"IOA {point.Ioa}: unknown point type.");
            if (!Enum.IsDefined(point.Command))
                problems.Add($"IOA {point.Ioa}: unknown command type.");
            if (point.Deadband < 0)
                problems.Add($"IOA {point.Ioa}: the deadband cannot be negative.");
            if (point.ReportScale == 0)
                problems.Add($"IOA {point.Ioa}: a report scale of 0 would send a constant 0.");

            // A status ASDU carrying an analogue value (or the reverse) is a configuration mistake
            // that would be very hard to spot from the control centre's side.
            var isStatusType = point.Type is Iec104PointType.SinglePoint or Iec104PointType.DoublePoint;
            if (isStatusType && tag.DataType != DataType.Bool)
                problems.Add($"IOA {point.Ioa} ('{tag.Path}'): {point.Type} carries a status bit, " +
                             $"but the tag is {tag.DataType}.");
            if (!isStatusType && tag.DataType == DataType.Bool)
                problems.Add($"IOA {point.Ioa} ('{tag.Path}'): {point.Type} carries a measurand, " +
                             $"but the tag is Bool — use SinglePoint or DoublePoint.");

            var isStatusCommand = point.Command is Iec104CommandType.SingleCommand or Iec104CommandType.DoubleCommand;
            if (isStatusCommand && tag.DataType != DataType.Bool)
                problems.Add($"IOA {point.Ioa} ('{tag.Path}'): {point.Command} writes a status bit, " +
                             $"but the tag is {tag.DataType}.");
        }

        return problems;
    }
}
