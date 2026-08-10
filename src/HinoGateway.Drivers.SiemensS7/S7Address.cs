using System.Text.RegularExpressions;

namespace HinoGateway.Drivers.SiemensS7;

public enum S7Area { Input, Output, Merker, DataBlock }

public sealed record S7Address(S7Area Area, int DbNumber, int ByteOffset, int? Bit, int? DeclaredSize)
{
    private static readonly Regex DbCanonical = new(@"^DB(?<db>\d+)\.(?<kind>DBX|DBB|DBW|DBD)(?<byte>\d+)(?:\.(?<bit>[0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DbNano = new(@"^DB(?<db>\d+)\[(?<byte>\d+)\](?:\.(?<bit>[0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AreaCanonical = new(@"^(?<kind>I|E|Q|A|M)(?<width>B|W|D)?(?<byte>\d+)(?:\.(?<bit>[0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static S7Address Parse(string text)
    {
        text = text?.Trim().Replace(" ", "") ?? "";
        var match = DbCanonical.Match(text);
        if (match.Success)
        {
            var kind = match.Groups["kind"].Value.ToUpperInvariant();
            int? bit = match.Groups["bit"].Success ? int.Parse(match.Groups["bit"].Value) : null;
            if (kind == "DBX" && bit is null) throw new FormatException("DBX address requires a bit index 0-7.");
            if (kind != "DBX" && bit is not null) throw new FormatException($"{kind} cannot include a bit index.");
            var size = kind switch { "DBX" or "DBB" => 1, "DBW" => 2, _ => 4 };
            return new(S7Area.DataBlock, int.Parse(match.Groups["db"].Value), int.Parse(match.Groups["byte"].Value), bit, size);
        }

        match = DbNano.Match(text);
        if (match.Success)
            return new(S7Area.DataBlock, int.Parse(match.Groups["db"].Value), int.Parse(match.Groups["byte"].Value),
                match.Groups["bit"].Success ? int.Parse(match.Groups["bit"].Value) : null, null);

        match = AreaCanonical.Match(text);
        if (match.Success)
        {
            var kind = match.Groups["kind"].Value.ToUpperInvariant();
            int? bit = match.Groups["bit"].Success ? int.Parse(match.Groups["bit"].Value) : null;
            var width = match.Groups["width"].Value;
            if (bit is not null && width.Length > 0) throw new FormatException("A bit address cannot include B/W/D width.");
            var area = kind switch { "I" or "E" => S7Area.Input, "Q" or "A" => S7Area.Output, _ => S7Area.Merker };
            var size = bit is not null ? 1 : width.ToUpperInvariant() switch { "B" => 1, "W" => 2, "D" => 4, _ => (int?)null };
            return new(area, 0, int.Parse(match.Groups["byte"].Value), bit, size);
        }

        throw new FormatException($"Unsupported S7 address '{text}'. Examples: DB1.DBX0.0, DB1.DBW2, DB1.DBD4, M0.0, MW2, IW0, QW0.");
    }
}
