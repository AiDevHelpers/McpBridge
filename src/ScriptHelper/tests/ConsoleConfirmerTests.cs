using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public class ConsoleConfirmerTests
{
    /// <summary>Records what had been written at the moment the answer was read.</summary>
    private sealed class RecordingReader : TextReader
    {
        private readonly StringWriter _output;
        private readonly string? _answer;

        public RecordingReader(StringWriter output, string? answer)
        {
            _output = output;
            _answer = answer;
        }

        public int Reads { get; private set; }
        public string? OutputAtRead { get; private set; }

        public override string? ReadLine()
        {
            Reads++;
            OutputAtRead = _output.ToString();
            return _answer;
        }
    }

    private static (bool Result, RecordingReader Reader, StringWriter Output) Ask(string? answer, bool redirected = false)
    {
        var output = new StringWriter();
        var reader = new RecordingReader(output, answer);
        var confirmer = new ConsoleConfirmer(reader, output, () => redirected);

        return (confirmer.Confirm("Approve this script to run elevated?"), reader, output);
    }

    [Fact]
    public void The_question_ends_its_line_before_the_answer_is_read()
    {
        // The wrapper window only shows complete lines, so a question without a newline would
        // appear after the answer was typed.
        var (_, reader, _) = Ask("y");

        Assert.EndsWith(Environment.NewLine, reader.OutputAtRead, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_and_question_are_shown_before_reading()
    {
        var (_, reader, _) = Ask("y");

        Assert.Contains("Approve this script to run elevated?", reader.OutputAtRead, StringComparison.Ordinal);
        Assert.Contains("Approve? [y/N]", reader.OutputAtRead, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("y", true)]
    [InlineData("Y", true)]
    [InlineData(" y ", true)]
    [InlineData("n", false)]
    [InlineData("", false)]
    [InlineData("yes", false)]
    [InlineData(null, false)]
    public void Only_an_explicit_y_approves(string? answer, bool expected)
    {
        var (result, _, _) = Ask(answer);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Redirected_input_is_never_a_yes_and_nothing_is_read_or_written()
    {
        var (result, reader, output) = Ask("y", redirected: true);

        Assert.False(result);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(string.Empty, output.ToString());
    }
}
