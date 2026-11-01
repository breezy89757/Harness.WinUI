using Xunit;

namespace Harness.Browser.Tests;

/// <summary>A generated test only counts as passed when a test really ran: "dotnet test" ends with 0 when its adapter finds nothing.</summary>
public class TestRunResultTests
{
    private static (int Executed, int Passed, int Failed)? Read(string counters)
    {
        var file = Path.Combine(Path.GetTempPath(), $"trx-{Guid.NewGuid():N}.trx");
        try
        {
            File.WriteAllText(file, $"<?xml version=\"1.0\"?><TestRun><ResultSummary outcome=\"Completed\">{counters}</ResultSummary></TestRun>");
            return CSharpScriptGenerator.ReadCounters(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Reads_the_counters_of_a_result_file()
    {
        Assert.Equal((1, 1, 0), Read("<Counters total=\"1\" executed=\"1\" passed=\"1\" error=\"0\" failed=\"0\" timeout=\"0\" />"));
        Assert.Equal((1, 0, 1), Read("<Counters total=\"1\" executed=\"1\" passed=\"0\" error=\"0\" failed=\"1\" timeout=\"0\" />"));
    }

    [Fact]
    public void A_run_that_executed_nothing_reads_as_zero_executed()
    {
        Assert.Equal((0, 0, 0), Read("<Counters total=\"0\" executed=\"0\" passed=\"0\" error=\"0\" failed=\"0\" timeout=\"0\" />"));
    }

    [Fact]
    public void A_missing_result_file_reads_as_unknown()
    {
        Assert.Null(CSharpScriptGenerator.ReadCounters(Path.Combine(Path.GetTempPath(), "no-such-file.trx")));
    }

    [Fact]
    public void The_generated_project_names_a_current_test_adapter()
    {
        Assert.Contains("MSTest.TestAdapter", CSharpScriptGenerator.ProjectFile, StringComparison.Ordinal);
        Assert.Contains("MSTest.TestFramework", CSharpScriptGenerator.ProjectFile, StringComparison.Ordinal);
    }
}
