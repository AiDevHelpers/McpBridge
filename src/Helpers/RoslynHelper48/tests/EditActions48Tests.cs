using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper48.Tests;

// Edit actions on the net48 host, through its pipe: the same plan, refuse and apply behaviour as the net10 helper.
public class EditActions48Tests
{
    private const string Source =
        "namespace Good;\n\npublic class Widget\n{\n    public string Name => \"good\";\n\n    public string Describe()\n    {\n        return \"good\";\n    }\n}\n";

    private const string NewMethod = "public string Describe() { return \"changed\"; }";

    [Fact]
    public void edit_method_on_net48_plans_without_writing_and_applies_with_the_shown_hash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rh48-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Good"));
        var solution = Path.Combine(dir, "App.slnx");
        var file = Path.Combine(dir, "Good", "Class1.cs");
        File.WriteAllText(solution, "<Solution>\n  <Project Path=\"Good/Good.csproj\" />\n</Solution>\n");
        File.WriteAllText(Path.Combine(dir, "Good", "Good.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup>\n</Project>\n");
        File.WriteAllText(file, Source);

        using var helper = Helper48Process.Start(dir, solution);
        var load = helper.Send("{\"action\":\"loadSolution\",\"args\":{\"solutionPath\":" + JsonSerializer.Serialize(solution) + "}}");
        Assert.True(JsonDocument.Parse(load).RootElement.GetProperty("ok").GetBoolean(), load);

        var editArgs = "\"file\":" + JsonSerializer.Serialize(file) + ",\"line\":9,\"column\":5,\"newMethodText\":" + JsonSerializer.Serialize(NewMethod);

        var shown = JsonDocument.Parse(helper.Send("{\"action\":\"editMethod\",\"args\":{" + editArgs + "}}")).RootElement.GetProperty("result");
        Assert.True(shown.GetProperty("planned").GetBoolean(), shown.GetRawText());
        Assert.False(shown.GetProperty("applied").GetBoolean());
        Assert.Equal(Source, File.ReadAllText(file));

        var hash = shown.GetProperty("check").GetProperty("plan_hash").GetString();
        var applied = JsonDocument.Parse(helper.Send(
            "{\"action\":\"editMethod\",\"args\":{" + editArgs + ",\"confirm\":true,\"planHash\":" + JsonSerializer.Serialize(hash) + "}}")).RootElement.GetProperty("result");
        Assert.True(applied.GetProperty("applied").GetBoolean(), applied.GetRawText());
        Assert.Contains("\"changed\"", File.ReadAllText(file));
    }
}
