using HinoGateway.Core;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// Utilities specify their signal lists in type identification numbers — EVN 378/QĐ-EVN asks for T13,
/// T30, T45 and T50 — so these mappings are what the configuration is checked against during
/// commissioning. Getting one wrong means a rejected signal list.
/// </summary>
public class Iec104TypeIdTests
{
    [Theory]
    // The four types EVN 378 names explicitly, plus their time-tagged counterparts.
    [InlineData(Iec104PointType.ShortFloat, false, 13, "M_ME_NC_1")]
    [InlineData(Iec104PointType.ShortFloat, true, 36, "M_ME_TF_1")]
    [InlineData(Iec104PointType.SinglePoint, true, 30, "M_SP_TB_1")]
    [InlineData(Iec104PointType.SinglePoint, false, 1, "M_SP_NA_1")]
    [InlineData(Iec104PointType.DoublePoint, false, 3, "M_DP_NA_1")]
    [InlineData(Iec104PointType.DoublePoint, true, 31, "M_DP_TB_1")]
    [InlineData(Iec104PointType.Normalized, false, 9, "M_ME_NA_1")]
    [InlineData(Iec104PointType.Normalized, true, 34, "M_ME_TD_1")]
    [InlineData(Iec104PointType.Scaled, false, 11, "M_ME_NB_1")]
    [InlineData(Iec104PointType.Scaled, true, 35, "M_ME_TE_1")]
    [InlineData(Iec104PointType.IntegratedTotals, false, 15, "M_IT_NA_1")]
    [InlineData(Iec104PointType.IntegratedTotals, true, 37, "M_IT_TB_1")]
    public void Monitored_types_carry_the_standard_number_and_name(
        Iec104PointType type, bool withTimestamp, int expectedNumber, string expectedName)
    {
        Assert.Equal(expectedNumber, Iec104TypeIds.Number(type, withTimestamp));
        Assert.Equal(expectedName, Iec104TypeIds.Name(type, withTimestamp));
        Assert.Equal($"T{expectedNumber} · {expectedName}", Iec104TypeIds.Label(type, withTimestamp));
    }

    [Theory]
    [InlineData(Iec104CommandType.SingleCommand, 45, "C_SC_NA_1")]
    [InlineData(Iec104CommandType.DoubleCommand, 46, "C_DC_NA_1")]
    [InlineData(Iec104CommandType.SetpointNormalized, 48, "C_SE_NA_1")]
    [InlineData(Iec104CommandType.SetpointScaled, 49, "C_SE_NB_1")]
    [InlineData(Iec104CommandType.SetpointShort, 50, "C_SE_NC_1")]
    public void Command_types_carry_the_standard_number_and_name(
        Iec104CommandType command, int expectedNumber, string expectedName)
    {
        Assert.Equal(expectedNumber, Iec104TypeIds.Number(command));
        Assert.Equal(expectedName, Iec104TypeIds.Name(command));
    }

    [Fact]
    public void Adding_a_time_tag_changes_the_type_which_is_why_it_is_shown_per_point()
    {
        // The exact trap EVN 378 sets: it asks for T13, and a time tag silently makes it T36.
        Assert.Equal(13, Iec104TypeIds.Number(Iec104PointType.ShortFloat, withTimestamp: false));
        Assert.Equal(36, Iec104TypeIds.Number(Iec104PointType.ShortFloat, withTimestamp: true));
    }

    [Theory]
    [InlineData("T13", Iec104PointType.ShortFloat)]
    [InlineData("13", Iec104PointType.ShortFloat)]
    [InlineData("t36", Iec104PointType.ShortFloat)]          // time-tagged form selects the same base
    [InlineData("M_ME_NC_1", Iec104PointType.ShortFloat)]
    [InlineData("m_me_tf_1", Iec104PointType.ShortFloat)]
    [InlineData("ShortFloat", Iec104PointType.ShortFloat)]
    [InlineData("T30", Iec104PointType.SinglePoint)]
    [InlineData("M_SP_TB_1", Iec104PointType.SinglePoint)]
    [InlineData("T15", Iec104PointType.IntegratedTotals)]
    [InlineData("  T11  ", Iec104PointType.Scaled)]
    public void A_signal_list_may_name_a_type_in_any_of_the_usual_ways(string text, Iec104PointType expected)
    {
        Assert.True(Iec104TypeIds.TryParsePointType(text, out var type));
        Assert.Equal(expected, type);
    }

    [Theory]
    [InlineData("T99")]
    [InlineData("M_XX_NA_1")]
    [InlineData("nonsense")]
    [InlineData("")]
    public void An_unrecognised_type_is_refused_rather_than_guessed(string text)
    {
        Assert.False(Iec104TypeIds.TryParsePointType(text, out _));
    }

    [Theory]
    [InlineData("T50", Iec104CommandType.SetpointShort)]
    [InlineData("50", Iec104CommandType.SetpointShort)]
    [InlineData("C_SE_NC_1", Iec104CommandType.SetpointShort)]
    [InlineData("SetpointShort", Iec104CommandType.SetpointShort)]
    [InlineData("T45", Iec104CommandType.SingleCommand)]
    [InlineData("None", Iec104CommandType.None)]
    [InlineData("", Iec104CommandType.None)]
    public void A_signal_list_may_name_a_command_in_any_of_the_usual_ways(string text, Iec104CommandType expected)
    {
        Assert.True(Iec104TypeIds.TryParseCommandType(text, out var command));
        Assert.Equal(expected, command);
    }

    [Fact]
    public void An_unrecognised_command_is_refused()
    {
        Assert.False(Iec104TypeIds.TryParseCommandType("T77", out _));
        Assert.False(Iec104TypeIds.TryParseCommandType("C_ZZ_NA_1", out _));
    }

    [Fact]
    public void The_link_is_off_until_someone_turns_it_on()
    {
        Assert.False(new Iec104ServerOptions().Enabled);
    }
}
