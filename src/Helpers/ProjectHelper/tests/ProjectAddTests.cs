using System.Text;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.Helpers.ProjectHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ProjectHelper.Tests;

/// <summary>A stand-in for the dotnet CLI: answers <c>new list</c>, creates the template files for <c>new</c>, and reports the build.</summary>
internal sealed class FakeDotnet
{
    public int BuildExit { get; set; }

    public RunCommand Run => (request, ct) => Task.FromResult(Handle(request));

    private CommandResult Handle(CommandRequest request)
    {
        var args = request.Arguments;
        if (args[0] == "new" && args[1] == "list")
        {
            return new CommandResult { StdOut = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dotnet-new-list.txt")) };
        }

        if (args[0] == "new")
        {
            var name = Value(args, "-n");
            var folder = Value(args, "-o");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(folder, "Class1.cs"), "class Class1 { }\n");
            return new CommandResult { StdOut = "The template was created." };
        }

        if (args[0] == "build")
        {
            return new CommandResult { ExitCode = BuildExit, StdOut = BuildExit == 0 ? "Build succeeded." : "error CS0001: boom" };
        }

        return new CommandResult { ExitCode = 1, StdErr = "unexpected command" };
    }

    private static string Value(IReadOnlyList<string> args, string key) => args[Array.IndexOf(args.ToArray(), key) + 1];
}

public sealed class ProjectAddTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "add-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDotnet _dotnet = new();

    private const string Solution = "AiDevHelpers.slnx";

    public ProjectAddTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));
        WriteStandards("[]");
        File.WriteAllText(Path.Combine(_root, Solution),
            "<Solution>\n  <Folder Name=\"/Core/\">\n    <Project Path=\"src/Core/Core.csproj\" />\n  </Folder>\n</Solution>\n");
        Csproj("src/Core/Core.csproj", "net10.0", "<Nullable>enable</Nullable>", "<PackageReference Include=\"Foo.Analyzers\" Version=\"1.0.0\" />");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteStandards(string exceptionsJson) =>
        File.WriteAllText(Path.Combine(_root, ".aidevhelpers", "project-standards.json"), """
        {
          "baseFramework": "net10.0",
          "allowedPlatformSuffixes": [ "-windows" ],
          "properties": { "Nullable": "enable" },
          "analyzers": [ "Foo.Analyzers" ],
          "exceptions": EXC
        }
        """.Replace("EXC", exceptionsJson));

    private void Csproj(string relative, string framework, string properties, string items)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>" + framework + "</TargetFramework>\n    "
            + properties + "\n  </PropertyGroup>\n  <ItemGroup>\n    " + items + "\n  </ItemGroup>\n</Project>\n");
    }

    private Task<AddPlanResult> PlanAsync(string name = "Lib", string? folder = "src/Lib", string? framework = null, string template = "classlib") =>
        ProjectAdder.PlanAsync(new AddRequest(_root, Solution, template, name, folder, "/Lib/", "C#", framework, []), _dotnet.Run, CancellationToken.None);

    [Fact]
    public async Task PlanReportsEditsAndWritesNothing()
    {
        var plan = await PlanAsync();

        Assert.NotNull(plan.Token);
        Assert.Empty(plan.Errors);
        Assert.Equal("src/Lib/Lib.csproj", plan.ProjectPath);
        Assert.Contains(plan.Edits, a => a.Kind == FixKind.SetProperty && a.Name == "Nullable");
        Assert.Contains(plan.Edits, a => a.Kind == FixKind.AddPackage && a.Name == "Foo.Analyzers" && a.Value == "1.0.0");
        Assert.False(Directory.Exists(Path.Combine(_root, "src", "Lib")));
    }

    [Fact]
    public async Task FrameworkOtherThanTheStandardIsRefused()
    {
        var plan = await PlanAsync(framework: "net8.0");

        Assert.Null(plan.Token);
        Assert.Contains(plan.Errors, e => e.Contains("net8.0"));
    }

    [Fact]
    public async Task ExceptionForTheNewPathAllowsTheFramework()
    {
        WriteStandards("[ { \"project\": \"src/Ex/Ex.csproj\", \"framework\": \"net8.0\", \"reason\": \"legacy host\" } ]");

        var plan = await PlanAsync(name: "Ex", folder: "src/Ex", framework: "net8.0");

        Assert.Empty(plan.Errors);
        Assert.NotNull(plan.Token);
    }

    [Fact]
    public async Task FolderIsRequiredWhenTheSolutionHasNoPattern()
    {
        var plan = await PlanAsync(folder: null);

        Assert.Null(plan.Token);
        Assert.Contains(plan.Errors, e => e.Contains("Folder"));
    }

    [Fact]
    public async Task NonEmptyFolderIsRefused()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "Lib"));
        File.WriteAllText(Path.Combine(_root, "src", "Lib", "notes.txt"), "keep");

        var plan = await PlanAsync();

        Assert.Null(plan.Token);
        Assert.NotEmpty(plan.Errors);
    }

    [Fact]
    public async Task UnknownTemplateIsRefused()
    {
        var plan = await PlanAsync(template: "no-such-template");

        Assert.Null(plan.Token);
        Assert.NotEmpty(plan.Errors);
    }

    [Fact]
    public async Task ConfirmCreatesTheProjectRemovesStubsAndAddsTheSolutionEntry()
    {
        var plan = await PlanAsync();

        var result = await ProjectAdder.ConfirmAsync(_root, plan.Token!, _dotnet.Run, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        var csproj = File.ReadAllText(Path.Combine(_root, "src", "Lib", "Lib.csproj"));
        Assert.Contains("<Nullable>enable</Nullable>", csproj);
        Assert.Contains("Foo.Analyzers", csproj);
        Assert.False(File.Exists(Path.Combine(_root, "src", "Lib", "Class1.cs")));
        var solution = File.ReadAllText(Path.Combine(_root, Solution));
        Assert.Contains("<Project Path=\"src/Lib/Lib.csproj\" />", solution);
    }

    [Fact]
    public async Task BuildFailureRemovesTheProjectAndRestoresTheSolution()
    {
        var before = File.ReadAllBytes(Path.Combine(_root, Solution));
        var plan = await PlanAsync();
        _dotnet.BuildExit = 1;

        var result = await ProjectAdder.ConfirmAsync(_root, plan.Token!, _dotnet.Run, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(Path.Combine(_root, "src", "Lib")));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_root, Solution)));
    }

    [Fact]
    public async Task SolutionChangedAfterPlanIsRefused()
    {
        var plan = await PlanAsync();
        File.AppendAllText(Path.Combine(_root, Solution), "\n<!-- edited -->\n");

        var result = await ProjectAdder.ConfirmAsync(_root, plan.Token!, _dotnet.Run, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(Path.Combine(_root, "src", "Lib")));
    }

    [Fact]
    public async Task TokenIsSingleUse()
    {
        var plan = await PlanAsync();
        _ = await ProjectAdder.ConfirmAsync(_root, plan.Token!, _dotnet.Run, CancellationToken.None);

        var again = await ProjectAdder.ConfirmAsync(_root, plan.Token!, _dotnet.Run, CancellationToken.None);

        Assert.False(again.Ok);
    }
}

public sealed class SlnxWriterTests
{
    private static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);

    private const string Base = "<Solution>\n  <Folder Name=\"/Core/\">\n    <Project Path=\"src/Core/Core.csproj\" />\n  </Folder>\n</Solution>\n";

    [Fact]
    public void InsertsIntoAnExistingFolder()
    {
        var text = Encoding.UTF8.GetString(SlnxWriter.Insert(Bytes(Base), "/Core/", "src/Core/Two.csproj"));

        Assert.Contains("    <Project Path=\"src/Core/Two.csproj\" />", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "<Folder Name=\"/Core/\""));
    }

    [Fact]
    public void CreatesAFolderWhenMissing()
    {
        var text = Encoding.UTF8.GetString(SlnxWriter.Insert(Bytes(Base), "/Lib/", "src/Lib/Lib.csproj"));

        Assert.Contains("<Folder Name=\"/Lib/\">", text);
        Assert.Contains("<Project Path=\"src/Lib/Lib.csproj\" />", text);
    }

    [Fact]
    public void AddsAtTheTopLevelWhenNoFolderIsGiven()
    {
        var text = Encoding.UTF8.GetString(SlnxWriter.Insert(Bytes(Base), null, "src/Top/Top.csproj"));

        Assert.Contains("<Project Path=\"src/Top/Top.csproj\" />", text);
    }

    [Fact]
    public void CrlfIsKept()
    {
        var crlf = Base.Replace("\n", "\r\n");

        var text = Encoding.UTF8.GetString(SlnxWriter.Insert(Bytes(crlf), "/Lib/", "src/Lib/Lib.csproj"));

        Assert.Equal(text.Count(c => c == '\n'), text.Count(c => c == '\r'));
    }

    [Fact]
    public void OnlyTheNewLineChanges()
    {
        var inserted = Encoding.UTF8.GetString(SlnxWriter.Insert(Bytes(Base), "/Core/", "src/Core/Two.csproj"));

        var expected = Base.Replace(
            "    <Project Path=\"src/Core/Core.csproj\" />\n",
            "    <Project Path=\"src/Core/Core.csproj\" />\n    <Project Path=\"src/Core/Two.csproj\" />\n");
        Assert.Equal(expected, inserted);
    }
}
