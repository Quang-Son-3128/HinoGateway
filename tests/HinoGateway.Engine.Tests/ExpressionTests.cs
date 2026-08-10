using HinoGateway.Core;
using HinoGateway.Expressions;
using Xunit;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// The formula language: how it parses, and — the part that matters more — what it does with the
/// quality of the values it reads. A gateway that quietly treats a dead sensor as zero reports half
/// the plant's power with complete confidence, so most of these tests are about refusing to do that.
/// </summary>
public class ExpressionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 10, 14, 30, 15, TimeSpan.FromHours(7));

    private static ExpressionValue Eval(string expression, Tags? tags = null, DateTimeOffset? now = null) =>
        ExpressionCompiler.Evaluate(expression, tags ?? new Tags(), now ?? T0);

    private static double Number(string expression, Tags? tags = null, DateTimeOffset? now = null)
    {
        var value = Eval(expression, tags, now);
        Assert.True(value.IsGood, $"expected a good number, got {value.Quality}: {value.Status}");
        Assert.True(value.TryAsNumber(out var number), $"expected a number, got {value.TypeName()}");
        return number;
    }

    // --- parsing --------------------------------------------------------------

    [Theory]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("10 - 2 - 3", 5)]          // left associative
    [InlineData("7 % 3", 1)]
    [InlineData("2 ^ 3 ^ 2", 512)]         // right associative
    [InlineData("2 ^ -1", 0.5)]
    [InlineData("1e3 + 1", 1001)]
    [InlineData(".5 * 4", 2)]
    public void Operators_group_the_way_they_are_written(string expression, double expected) =>
        Assert.Equal(expected, Number(expression), 9);

    /// <summary>
    /// The classic trap. If exponentiation bound less tightly than unary minus this would be 4, and
    /// nothing would ever report an error — the formula would simply compute a different number.
    /// </summary>
    [Fact]
    public void Power_binds_tighter_than_unary_minus() => Assert.Equal(-4, Number("-2^2"));

    [Fact]
    public void Comments_and_line_breaks_are_allowed_inside_a_formula() =>
        Assert.Equal(3, Number("1 + // the first part\n 2"));

    [Fact]
    public void Comparison_and_logic_produce_booleans()
    {
        Assert.True(Eval("3 > 2 && 1 < 2").TryAsBool(out var and) && and);
        Assert.True(Eval("1 == 1.0").TryAsBool(out var equal) && equal);
        Assert.True(Eval("1 <> 2").TryAsBool(out var different) && different);   // '<>' is '!='
    }

    [Fact]
    public void A_bare_word_says_what_was_probably_meant()
    {
        var error = Assert.Throws<ExpressionSyntaxException>(() => ExpressionCompiler.Compile("Temp * 2"));
        Assert.Contains("{Temp}", error.Message);
        Assert.Equal(0, error.Position);
    }

    [Fact]
    public void An_unknown_function_is_refused_at_compile_time() =>
        Assert.Contains("no function called 'avgg'",
            Assert.Throws<ExpressionSyntaxException>(() => ExpressionCompiler.Compile("avgg(1,2)")).Message);

    [Fact]
    public void A_function_called_with_the_wrong_number_of_arguments_is_refused() =>
        Assert.Contains("takes exactly 3 arguments",
            Assert.Throws<ExpressionSyntaxException>(() => ExpressionCompiler.Compile("if(true, 1)")).Message);

    [Fact]
    public void The_error_carries_the_character_it_was_found_at()
    {
        var error = Assert.Throws<ExpressionSyntaxException>(() => ExpressionCompiler.Compile("1 + * 2"));
        Assert.Equal(4, error.Position);
        Assert.Contains("character 5", error.Describe());
    }

    [Fact]
    public void The_tags_a_formula_reads_are_known_before_it_ever_runs()
    {
        var compiled = ExpressionCompiler.Compile("({Plant/P1} + {Plant/P2}) / 1000 + {Plant/P1}");
        Assert.Equal(new[] { "Plant/P1", "Plant/P2" }, compiled.ReferencedTags.OrderBy(t => t));
    }

    // --- quality propagation --------------------------------------------------

    [Fact]
    public void Arithmetic_takes_the_worst_quality_of_its_operands()
    {
        var tags = new Tags().Good("P1", 100.0).Uncertain("P2", 50.0, "stale");
        var value = Eval("{P1} + {P2}", tags);

        Assert.Equal(Quality.Uncertain, value.Quality);
        Assert.Equal(150.0, value.Value);
        Assert.Contains("P2", value.Status);
    }

    [Fact]
    public void A_bad_input_makes_the_result_bad_rather_than_counting_as_zero()
    {
        var tags = new Tags().Good("P1", 100.0).Bad("P2", "timeout");
        var value = Eval("{P1} + {P2}", tags);

        Assert.Equal(Quality.Bad, value.Quality);
        Assert.Null(value.Value);
        Assert.Contains("P2", value.Status);
        Assert.Contains("timeout", value.Status);
    }

    [Fact]
    public void A_tag_that_does_not_exist_says_so_instead_of_reading_as_bad()
    {
        var value = Eval("{Nowhere} + 1");
        Assert.Equal(Quality.Bad, value.Quality);
        Assert.Contains("does not exist", value.Status);
    }

    [Fact]
    public void And_or_do_not_evaluate_the_side_they_did_not_need()
    {
        // The whole point of writing it this way: Flow is not consulted while the line is disabled,
        // so its quality cannot spoil the answer.
        var tags = new Tags().Good("Enabled", false).Bad("Flow", "sensor offline");
        var value = Eval("{Enabled} && {Flow} > 0", tags);

        Assert.True(value.IsGood);
        Assert.Equal(false, value.Value);
    }

    [Fact]
    public void The_branch_not_taken_may_be_bad()
    {
        var tags = new Tags().Bad("Backup", "offline").Good("Main", 42.0);
        var value = Eval("good({Main}) ? {Main} : {Backup}", tags);

        Assert.True(value.IsGood);
        Assert.Equal(42.0, value.Value);
    }

    [Fact]
    public void The_condition_quality_still_counts_towards_the_answer()
    {
        var tags = new Tags().Uncertain("Mode", true, "stale").Good("A", 1.0);
        var value = Eval("{Mode} ? {A} : 0", tags);

        Assert.Equal(Quality.Uncertain, value.Quality);
        Assert.Equal(1.0, value.Value);
    }

    [Fact]
    public void Quality_questions_answer_good_so_a_fallback_can_be_chosen()
    {
        var tags = new Tags().Bad("T1", "offline").Good("T2", 20.0);

        Assert.Equal(20.0, Eval("if(bad({T1}), {T2}, {T1})", tags).Value);
        Assert.Equal(0.0, Eval("quality({T1})", tags).Value);
        Assert.Equal(2.0, Eval("quality({T2})", tags).Value);
        Assert.True(Eval("bad({T1})", tags).IsGood);
    }

    [Fact]
    public void Coalesce_takes_the_first_good_input()
    {
        var tags = new Tags().Bad("Primary", "offline").Uncertain("Standby", 7.0, "stale").Good("Third", 9.0);
        Assert.Equal(9.0, Eval("coalesce({Primary}, {Standby}, {Third})", tags).Value);

        var none = Eval("coalesce({Primary}, {Standby})", tags);
        Assert.Equal(Quality.Bad, none.Quality);
        Assert.Contains("none of the 2 inputs", none.Status);
    }

    [Fact]
    public void Forcegood_is_the_explicit_way_to_keep_a_doubtful_value()
    {
        var tags = new Tags().Uncertain("T", 12.0, "stale").Bad("Dead", "offline");

        Assert.True(Eval("forcegood({T})", tags).IsGood);
        Assert.Equal(12.0, Eval("forcegood({T})", tags).Value);
        Assert.Equal(0.0, Eval("forcegood({Dead}, 0)", tags).Value);
        Assert.Equal(Quality.Bad, Eval("forcegood({Dead})", tags).Quality);
    }

    // --- arithmetic that has no answer ---------------------------------------

    [Fact]
    public void Division_by_zero_is_bad_with_a_reason_rather_than_infinity()
    {
        var value = Eval("10 / 0");
        Assert.Equal(Quality.Bad, value.Quality);
        Assert.Equal("division by zero", value.Status);
    }

    [Fact]
    public void A_result_that_is_not_a_finite_number_is_refused() =>
        Assert.Equal(Quality.Bad, Eval("1e308 * 10").Quality);

    [Fact]
    public void The_domain_of_a_function_is_reported_in_words() =>
        Assert.Contains("square root of a negative", Eval("sqrt(-1)").Status);

    // --- types ----------------------------------------------------------------

    [Fact]
    public void Text_is_never_read_as_a_number_by_accident()
    {
        var tags = new Tags().Good("Serial", "12A");
        Assert.Equal(Quality.Bad, Eval("{Serial} * 2", tags).Quality);
        Assert.Equal(24.0, Eval("tonumber(\"12\") * 2").Value);
        Assert.Equal(0.0, Eval("tonumber({Serial}, 0)", tags).Value);
    }

    [Fact]
    public void Adding_to_text_joins_it()
    {
        var tags = new Tags().Good("Name", "Line ").Good("N", 2.0);
        Assert.Equal("Line 2", Eval("{Name} + {N}", tags).Value);
    }

    [Fact]
    public void A_boolean_counts_as_one_or_zero_in_arithmetic()
    {
        var tags = new Tags().Good("Run", true).Good("P", 50.0);
        Assert.Equal(50.0, Eval("{Run} * {P}", tags).Value);
    }

    // --- functions ------------------------------------------------------------

    [Fact]
    public void Engineering_helpers_do_what_their_names_say()
    {
        Assert.Equal(50, Number("scale(12, 4, 20, 0, 100)"), 9);
        Assert.Equal(100, Number("clamp(150, 0, 100)"), 9);
        Assert.Equal(3, Number("round(2.5)"), 9);          // away from zero, not banker's
        Assert.Equal(1.23, Number("round(1.2345, 2)"), 9);
        Assert.Equal(2, Number("min(5, 2, 9)"), 9);
        Assert.Equal(5, Number("avg(2, 4, 9)"), 9);
        Assert.Equal(90, Number("deg(rad(90))"), 9);
    }

    [Fact]
    public void Text_functions_count_the_first_character_as_one()
    {
        Assert.Equal("BC", Eval("substr(\"ABCD\", 2, 2)").Value);
        Assert.Equal("ABCD", Eval("substr(\"ABCD\", 1)").Value);
        Assert.Equal("", Eval("substr(\"AB\", 9)").Value);   // past the end is empty, not an error
        Assert.Equal(4.0, Eval("len(\"ABCD\")").Value);
        Assert.Equal("A-B", Eval("concat(\"A\", \"-\", \"B\")").Value);
    }

    [Fact]
    public void Time_functions_answer_about_the_local_clock_of_the_evaluation()
    {
        Assert.Equal(14, Number("hour()"));
        Assert.Equal(30, Number("minute()"));
        Assert.Equal(10, Number("day()"));
        Assert.Equal(8, Number("month()"));
        Assert.Equal(2026, Number("year()"));
        Assert.Equal(1, Number("dayofweek()"));   // 2026-08-10 is a Monday
        Assert.Equal("2026-08-10 14:30:15", Eval("timestamp()").Value);
    }

    // --- history --------------------------------------------------------------

    [Fact]
    public void Prev_and_dt_report_nothing_on_the_first_evaluation()
    {
        var compiled = ExpressionCompiler.Compile("({Counter} - prev({Counter})) / dt()");
        var tags = new Tags().Good("Counter", 100.0);
        var memory = compiled.CreateMemory();

        var first = compiled.Evaluate(new EvaluationContext(tags, T0, memory));
        Assert.Equal(Quality.Bad, first.Quality);

        // A rate of change: 60 more units, 60 seconds later.
        tags.Good("Counter", 160.0);
        var second = compiled.Evaluate(new EvaluationContext(tags, T0.AddSeconds(60), memory));
        Assert.True(second.IsGood);
        Assert.Equal(1.0, (double)second.Value!, 9);
    }

    [Fact]
    public void Two_prev_calls_keep_separate_histories()
    {
        var compiled = ExpressionCompiler.Compile("prev({A}) * 10 + prev({B})");
        Assert.Equal(2, compiled.PreviousSlotCount);

        var tags = new Tags().Good("A", 1.0).Good("B", 2.0);
        var memory = compiled.CreateMemory();
        compiled.Evaluate(new EvaluationContext(tags, T0, memory));

        tags.Good("A", 9.0).Good("B", 9.0);
        var second = compiled.Evaluate(new EvaluationContext(tags, T0.AddSeconds(1), memory));
        Assert.Equal(12.0, second.Value);   // 1*10 + 2, each slot its own
    }

    [Fact]
    public void Age_reports_how_old_a_reading_is_whatever_its_quality()
    {
        var tags = new Tags().At("T", 5.0, T0.AddSeconds(-90));
        var value = Eval("age({T})", tags, T0);

        Assert.True(value.IsGood);
        Assert.Equal(90.0, (double)value.Value!, 3);
        Assert.True(Eval("age({T}) > 60", tags, T0).Value as bool?);
    }

    /// <summary>
    /// age() reads a timestamp, and only a tag reference carries one — so it is refused while the
    /// formula is being written rather than going Bad in the field.
    /// </summary>
    [Fact]
    public void Age_insists_on_a_tag_reference() =>
        Assert.Contains("tag reference",
            Assert.Throws<ExpressionSyntaxException>(() => ExpressionCompiler.Compile("age(1 + 2)")).Message);

    private sealed class Tags : ITagValueSource
    {
        private readonly Dictionary<string, TagValue> _values = new(StringComparer.Ordinal);

        public Tags Good(string id, object value)
        {
            _values[id] = TagValue.Good(value, T0);
            return this;
        }

        public Tags At(string id, object value, DateTimeOffset timestamp)
        {
            _values[id] = TagValue.Good(value, timestamp);
            return this;
        }

        public Tags Bad(string id, string reason)
        {
            _values[id] = TagValue.Bad(reason, T0);
            return this;
        }

        public Tags Uncertain(string id, object value, string reason)
        {
            _values[id] = new TagValue
            {
                Value = value,
                Quality = Quality.Uncertain,
                StatusText = reason,
                SourceTimestamp = T0,
                LastChanged = T0,
            };
            return this;
        }

        public bool TryGet(string tagId, out TagValue value) => _values.TryGetValue(tagId, out value!);
    }
}
