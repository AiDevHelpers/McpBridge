using System.Text.Json;
using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class MethodTestRunnerTests
{
    [Fact]
    public async Task ARepliesThatWasNotAppliedIsReturnedUnchanged()
    {
        const string reply = "{\"ok\":true,\"solution_path\":\"C:\\\\s\\\\a.slnx\",\"result\":{\"planned\":true,\"applied\":false,\"check\":{}}}";

        Assert.Equal(reply, await MethodTestRunner.AttachAsync(reply, true, CancellationToken.None));
    }

    [Fact]
    public async Task ADisabledRunAndAReplyThatIsNotJsonAreReturnedUnchanged()
    {
        const string applied = "{\"solution_path\":\"C:\\\\s\\\\a.slnx\",\"result\":{\"applied\":true,\"check\":{}}}";

        Assert.Equal(applied, await MethodTestRunner.AttachAsync(applied, false, CancellationToken.None));
        Assert.Equal("not json", await MethodTestRunner.AttachAsync("not json", true, CancellationToken.None));
    }

    [Fact]
    public async Task WithNoTestAndNoTestProjectNothingRunsAndItIsSaid()
    {
        var folder = Directory.CreateTempSubdirectory("methodtests").FullName;
        try
        {
            using var check = JsonDocument.Parse("{\"tests\":[],\"files\":[]}");

            var run = await MethodTestRunner.RunAsync(Path.Combine(folder, "a.slnx"), check.RootElement, null, CancellationToken.None);

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(run));
            Assert.False(json.RootElement.GetProperty("ran").GetBoolean());
            Assert.Equal("none", json.RootElement.GetProperty("scope").GetString());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task AnAppliedReplyWhoseRunFailsGetsATestRunErrorNotAnException()
    {
        var folder = Directory.CreateTempSubdirectory("methodtests").FullName;
        try
        {
            var solution = Path.Combine(folder, "a.slnx").Replace("\\", "\\\\");
            var reply = "{\"solution_path\":\"" + solution + "\",\"result\":{\"applied\":true,\"check\":{\"tests\":[],\"files\":[]}}}";

            var merged = await MethodTestRunner.AttachAsync(reply, true, CancellationToken.None);

            using var json = JsonDocument.Parse(merged);
            Assert.False(json.RootElement.GetProperty("test_run").GetProperty("ran").GetBoolean());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
