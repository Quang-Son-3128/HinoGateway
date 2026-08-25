using HinoGateway.Core;
using HinoGateway.Expressions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// The command language: what a script may say, what it refuses to say, and what it does with a
/// reading it cannot trust.
///
/// <para>
/// The refusals are the point of most of these. A command is the one thing in the gateway that
/// writes to a PLC on somebody else's say-so — the utility's control centre sets a tag and a script
/// decides what that means for the plant — so "this script cannot express that" is a safety property,
/// not a limitation to be worked around later.
/// </para>
/// </summary>
public class ScriptTests
{
    private sealed class Tags : ITagValueSource
    {
        private readonly Dictionary<string, TagValue> _values = new(StringComparer.Ordinal);

        public Tags Set(string path, object? value, Quality quality = Quality.Good)
        {
            _values[path] = quality == Quality.Good
                ? TagValue.Good(value!, DateTimeOffset.UtcNow)
                : new TagValue
                {
                    Value = quality == Quality.Bad ? null : value,
                    Quality = quality,
                    SourceTimestamp = DateTimeOffset.UtcNow,
                    LastChanged = DateTimeOffset.UtcNow,
                    StatusText = "test",
                };
            return this;
        }

        public bool TryGet(string tagId, out TagValue value) => _values.TryGetValue(tagId, out value!);
    }

    private static ScriptRun Run(string script, Tags tags, object? value = null)
    {
        var compiled = ScriptCompiler.Compile(script);
        return compiled.Run(tags, ExpressionValue.Good(value ?? 0d), DateTimeOffset.Now);
    }

    [Fact]
    public void A_command_writes_what_it_computes()
    {
        var tags = new Tags().Set("PLCControl/PLoad", 40.0);

        var run = Run("""
            var load = Tag("PLCControl/PLoad");
            Write("PLCControl/SettingPEVN", load + value);
            """, tags, value: 60.0);

        Assert.True(run.Completed);
        var write = Assert.Single(run.Writes);
        Assert.Equal("PLCControl/SettingPEVN", write.TagPath);
        Assert.Equal(100.0, Assert.IsType<double>(write.Value), 6);
    }

    /// <summary>The EVN curtailment command, as it will actually be written.</summary>
    [Fact]
    public void The_real_curtailment_command_compiles_and_runs()
    {
        var tags = new Tags()
            .Set("PLCControl/EnableEVNMode", true)
            .Set("PLCControl/TotalInvOn", 4.0);

        var run = Run("""
            if (Tag("PLCControl/EnableEVNMode") != true) return;
            if (Tag("PLCControl/TotalInvOn") == 0) return;
            Write("PLCControl/SettingPEVN", 165 * value / 100);
            """, tags, value: 50.0);

        Assert.True(run.Completed);
        Assert.Equal(82.5, Assert.IsType<double>(Assert.Single(run.Writes).Value), 6);
    }

    [Fact]
    public void Return_stops_the_command()
    {
        var tags = new Tags().Set("PLCControl/EnableEVNMode", false);

        var run = Run("""
            if (Tag("PLCControl/EnableEVNMode") != true) return;
            Write("PLCControl/SettingPEVN", value);
            """, tags, value: 50.0);

        Assert.True(run.Completed);
        Assert.Empty(run.Writes);
    }

    [Fact]
    public void Else_runs_the_other_branch()
    {
        var tags = new Tags().Set("Plant/Auto", false);

        var run = Run("""
            if (Tag("Plant/Auto") == true) {
                Write("Plant/Setpoint", 100);
            } else {
                Write("Plant/Setpoint", 0);
            }
            """, tags);

        Assert.Equal(0.0, Assert.IsType<double>(Assert.Single(run.Writes).Value), 6);
    }

    /// <summary>
    /// The one runtime rule that exists to stop damage rather than confusion: a setpoint computed
    /// from a dead reading is not written, and the command says so.
    /// </summary>
    [Fact]
    public void A_value_computed_from_a_bad_reading_is_not_written()
    {
        var tags = new Tags().Set("PLCControl/PLoad", 40.0, Quality.Bad);

        var run = Run("""
            Write("PLCControl/SettingPEVN", Tag("PLCControl/PLoad") + value);
            """, tags, value: 10.0);

        Assert.False(run.Completed);
        Assert.Empty(run.Writes);
        Assert.Contains("refused to write", run.Error);
    }

    [Fact]
    public void A_condition_that_cannot_be_decided_stops_the_command()
    {
        var tags = new Tags().Set("Plant/Auto", true, Quality.Bad);

        var run = Run("""
            if (Tag("Plant/Auto") == true) { Write("Plant/Setpoint", 1); }
            """, tags);

        Assert.False(run.Completed);
        Assert.Empty(run.Writes);
    }

    [Fact]
    public void What_it_reads_and_writes_is_known_before_it_runs()
    {
        var compiled = ScriptCompiler.Compile("""
            var a = Tag("Plant/A");
            if (a > 0) { Write("Plant/X", a); } else { Write("Plant/Y", 0); }
            """);

        Assert.Equal(new[] { "Plant/A" }, compiled.ReadTags.OrderBy(x => x));
        Assert.Equal(new[] { "Plant/X", "Plant/Y" }, compiled.WrittenTags.OrderBy(x => x));
    }

    [Theory]
    // A path that is not a literal would make "what can this command write to" unanswerable
    // without running it, which is the property the validator and the UI are built on.
    [InlineData("""Write(Tag("Plant/Name"), 1);""", "written out as text")]
    [InlineData("""var p = "Plant/X"; Write(p, 1);""", "written out as text")]
    // No loops, on purpose.
    [InlineData("""while (true) { Write("Plant/X", 1); }""", "a command has no loops")]
    [InlineData("""for (;;) { }""", "a command has no loops")]
    // Ordinary slips, with a message that says what to do.
    [InlineData("""Write("Plant/X", 1)""", "';' is missing")]
    [InlineData("""x = 1;""", "has not been declared")]
    [InlineData("""var x = 1; var x = 2;""", "already declared")]
    [InlineData("""value = 1;""", "cannot be assigned")]
    [InlineData("""return 5;""", "takes no value")]
    [InlineData("""if (true) { Write("Plant/X", 1);""", "'}' is missing")]
    [InlineData("""var x = Write("Plant/X", 1);""", "is a statement, not a value")]
    [InlineData("", "does nothing")]
    public void What_the_language_refuses(string script, string expected)
    {
        Assert.False(ScriptCompiler.TryCompile(script, out _, out var error, out _));
        Assert.Contains(expected, error);
    }

    /// <summary>
    /// The ceilings the 2026-08-21 security review added to the expression parser have to cover this
    /// grammar too. A stack overflow is not catchable in .NET: it ends the gateway, and a command
    /// that overflows would do it again on every restart once it was saved.
    /// </summary>
    [Fact]
    public void Nesting_deeper_than_the_parser_allows_is_refused_rather_than_overflowing()
    {
        var script = string.Concat(Enumerable.Repeat("if (true) { ", 200))
                     + """Write("Plant/X", 1);"""
                     + string.Concat(Enumerable.Repeat(" }", 200));

        Assert.False(ScriptCompiler.TryCompile(script, out _, out var error, out _));
        Assert.Contains("nests more than", error);
    }

    [Fact]
    public void A_command_longer_than_the_ceiling_is_refused()
    {
        var script = new string(' ', ScriptCompiler.MaxScriptLength + 1);

        Assert.False(ScriptCompiler.TryCompile(script, out _, out var error, out _));
        Assert.Contains("the limit is", error);
    }

    [Fact]
    public void More_statements_than_the_ceiling_is_refused()
    {
        // 250, not 300: past that the lexer's own token ceiling fires first and the message is
        // about tokens rather than statements. Both bounds are real; this test is about this one.
        var script = string.Concat(Enumerable.Repeat("""Write("Plant/X", 1);""", 250));

        Assert.False(ScriptCompiler.TryCompile(script, out _, out var error, out _));
        Assert.Contains("statements long", error);
    }

    [Fact]
    public void More_writes_than_the_run_allows_is_refused()
    {
        var compiled = ScriptCompiler.Compile(string.Concat(Enumerable.Repeat("""Write("Plant/X", 1);""", 10)));

        var run = compiled.Run(new Tags(), ExpressionValue.Good(0d), DateTimeOffset.Now, maxWrites: 4);

        Assert.False(run.Completed);
        Assert.Equal(4, run.Writes.Count);
        Assert.Contains("more than 4 values", run.Error);
    }

    /// <summary>Formulas are unaffected: '{' is still a tag reference and '=' is still a mistake.</summary>
    [Fact]
    public void The_formula_language_is_unchanged()
    {
        Assert.True(ExpressionCompiler.TryCompile("{Plant/A} + {Plant/B}", out var compiled, out _, out _));
        Assert.Equal(new[] { "Plant/A", "Plant/B" }, compiled!.ReferencedTags.OrderBy(x => x));

        Assert.False(ExpressionCompiler.TryCompile("{Plant/A} = 1", out _, out var error, out _));
        Assert.Contains("use '=='", error);

        // A bare word in a formula is still refused, with the message that offers both fixes.
        Assert.False(ExpressionCompiler.TryCompile("load + 1", out _, out var bareWord, out _));
        Assert.Contains("is not a value", bareWord);
    }
}
