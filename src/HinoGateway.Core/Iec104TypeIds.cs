namespace HinoGateway.Core;

/// <summary>
/// The type identification numbers of IEC 60870-5-101/104, and their standard names.
///
/// Utilities write their signal lists in these numbers — EVN 378/QĐ-EVN asks for "T13", "T30",
/// "T45", "T50" — so the configuration speaks the same language rather than making an engineer
/// translate an internal enum in their head while checking hundreds of points.
///
/// Note that adding a time tag changes the type: a short float measurand is T13 without one and T36
/// with one. That is exactly the sort of detail a control centre rejects a signal list over, so the
/// effective number is always derived from the point's own settings, never assumed.
/// </summary>
public static class Iec104TypeIds
{
    /// <summary>Type identification for a monitored point, given whether it carries a time tag.</summary>
    public static int Number(Iec104PointType type, bool withTimestamp) => (type, withTimestamp) switch
    {
        (Iec104PointType.SinglePoint, false) => 1,        // M_SP_NA_1
        (Iec104PointType.SinglePoint, true) => 30,        // M_SP_TB_1
        (Iec104PointType.DoublePoint, false) => 3,        // M_DP_NA_1
        (Iec104PointType.DoublePoint, true) => 31,        // M_DP_TB_1
        (Iec104PointType.Normalized, false) => 9,         // M_ME_NA_1
        (Iec104PointType.Normalized, true) => 34,         // M_ME_TD_1
        (Iec104PointType.Scaled, false) => 11,            // M_ME_NB_1
        (Iec104PointType.Scaled, true) => 35,             // M_ME_TE_1
        (Iec104PointType.ShortFloat, false) => 13,        // M_ME_NC_1
        (Iec104PointType.ShortFloat, true) => 36,         // M_ME_TF_1
        (Iec104PointType.IntegratedTotals, false) => 15,  // M_IT_NA_1
        _ => 37,                                          // M_IT_TB_1
    };

    /// <summary>Standard name for a monitored point, e.g. "M_ME_NC_1".</summary>
    public static string Name(Iec104PointType type, bool withTimestamp) => (type, withTimestamp) switch
    {
        (Iec104PointType.SinglePoint, false) => "M_SP_NA_1",
        (Iec104PointType.SinglePoint, true) => "M_SP_TB_1",
        (Iec104PointType.DoublePoint, false) => "M_DP_NA_1",
        (Iec104PointType.DoublePoint, true) => "M_DP_TB_1",
        (Iec104PointType.Normalized, false) => "M_ME_NA_1",
        (Iec104PointType.Normalized, true) => "M_ME_TD_1",
        (Iec104PointType.Scaled, false) => "M_ME_NB_1",
        (Iec104PointType.Scaled, true) => "M_ME_TE_1",
        (Iec104PointType.ShortFloat, false) => "M_ME_NC_1",
        (Iec104PointType.ShortFloat, true) => "M_ME_TF_1",
        (Iec104PointType.IntegratedTotals, false) => "M_IT_NA_1",
        _ => "M_IT_TB_1",
    };

    /// <summary>"T13 · M_ME_NC_1" — what the configuration screens and CSV comments show.</summary>
    public static string Label(Iec104PointType type, bool withTimestamp) =>
        $"T{Number(type, withTimestamp)} · {Name(type, withTimestamp)}";

    public static int Number(Iec104CommandType command) => command switch
    {
        Iec104CommandType.SingleCommand => 45,        // C_SC_NA_1
        Iec104CommandType.DoubleCommand => 46,        // C_DC_NA_1
        Iec104CommandType.SetpointNormalized => 48,   // C_SE_NA_1
        Iec104CommandType.SetpointScaled => 49,       // C_SE_NB_1
        Iec104CommandType.SetpointShort => 50,        // C_SE_NC_1
        _ => 0,
    };

    public static string Name(Iec104CommandType command) => command switch
    {
        Iec104CommandType.SingleCommand => "C_SC_NA_1",
        Iec104CommandType.DoubleCommand => "C_DC_NA_1",
        Iec104CommandType.SetpointNormalized => "C_SE_NA_1",
        Iec104CommandType.SetpointScaled => "C_SE_NB_1",
        Iec104CommandType.SetpointShort => "C_SE_NC_1",
        _ => "none",
    };

    public static string Label(Iec104CommandType command) =>
        command == Iec104CommandType.None ? "— none —" : $"T{Number(command)} · {Name(command)}";

    /// <summary>
    /// Accept whichever way a signal list names a monitored type: the internal name ("ShortFloat"),
    /// the standard name ("M_ME_NC_1") or the utility's number ("T13" / "13"). Time-tagged numbers are
    /// accepted too and simply select the same base type — the time tag is its own column.
    /// </summary>
    public static bool TryParsePointType(string text, out Iec104PointType type)
    {
        type = Iec104PointType.ShortFloat;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();

        // Numbers first, and never through Enum.TryParse: that happily turns "50" into an undefined
        // enum member, so a type identification number would be silently accepted as garbage.
        var withoutPrefix = value.StartsWith("T", StringComparison.OrdinalIgnoreCase) ? value[1..] : value;
        if (int.TryParse(withoutPrefix, out var number))
        {
            switch (number)
            {
                case 1 or 2 or 30: type = Iec104PointType.SinglePoint; return true;
                case 3 or 4 or 31: type = Iec104PointType.DoublePoint; return true;
                case 9 or 10 or 34: type = Iec104PointType.Normalized; return true;
                case 11 or 12 or 35: type = Iec104PointType.Scaled; return true;
                case 13 or 14 or 36: type = Iec104PointType.ShortFloat; return true;
                case 15 or 16 or 37: type = Iec104PointType.IntegratedTotals; return true;
                default: return false;
            }
        }

        if (Enum.TryParse(value, ignoreCase: true, out type) && Enum.IsDefined(type))
            return true;

        foreach (var candidate in Enum.GetValues<Iec104PointType>())
        {
            if (Name(candidate, false).Equals(value, StringComparison.OrdinalIgnoreCase)
                || Name(candidate, true).Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                type = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The same for commands: "SetpointShort", "C_SE_NC_1", "T50" or "50" — and "None".</summary>
    public static bool TryParseCommandType(string text, out Iec104CommandType command)
    {
        command = Iec104CommandType.None;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var value = text.Trim();

        // Same trap as above: "50" must mean C_SE_NC_1, not an undefined enum member.
        var withoutPrefix = value.StartsWith("T", StringComparison.OrdinalIgnoreCase) ? value[1..] : value;
        if (int.TryParse(withoutPrefix, out var number))
        {
            switch (number)
            {
                case 0: command = Iec104CommandType.None; return true;
                case 45 or 58: command = Iec104CommandType.SingleCommand; return true;
                case 46 or 59: command = Iec104CommandType.DoubleCommand; return true;
                case 48 or 61: command = Iec104CommandType.SetpointNormalized; return true;
                case 49 or 62: command = Iec104CommandType.SetpointScaled; return true;
                case 50 or 63: command = Iec104CommandType.SetpointShort; return true;
                default: return false;
            }
        }

        if (Enum.TryParse(value, ignoreCase: true, out command) && Enum.IsDefined(command))
            return true;

        foreach (var candidate in Enum.GetValues<Iec104CommandType>())
        {
            if (Name(candidate).Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                command = candidate;
                return true;
            }
        }

        return false;
    }
}
