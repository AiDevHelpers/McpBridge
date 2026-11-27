using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper48.Tests;

// The net48 host loads a solution with the Framework MSBuild: a legacy net48 project, an SDK-style net48 project and a
// broken project in one solution. Each project reports its own result. The load runs inside the real helper process,
// because the .NET Framework build host must sit beside the helper's own assemblies, which a test runner does not provide.
public class SolutionLoad48Tests
{
    [Fact]
    public void a_legacy_project_and_an_sdk_project_load_and_a_broken_one_reports_its_own_error()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rh48-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Legacy"));
        Directory.CreateDirectory(Path.Combine(dir, "Sdk"));
        Directory.CreateDirectory(Path.Combine(dir, "Broken"));
        var solution = Path.Combine(dir, "App.slnx");

        File.WriteAllText(solution,
            "<Solution>\n  <Project Path=\"Legacy/Legacy.csproj\" />\n  <Project Path=\"Sdk/Sdk.csproj\" />\n  <Project Path=\"Broken/Broken.csproj\" />\n</Solution>\n");
        File.WriteAllText(Path.Combine(dir, "Legacy", "Legacy.csproj"),
            "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
            "  <PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion><OutputType>Library</OutputType><AssemblyName>Legacy</AssemblyName></PropertyGroup>\n" +
            "  <ItemGroup><Compile Include=\"Class1.cs\" /></ItemGroup>\n" +
            "  <Import Project=\"$(MSBuildToolsPath)\\Microsoft.CSharp.targets\" />\n" +
            "</Project>\n");
        File.WriteAllText(Path.Combine(dir, "Legacy", "Class1.cs"), "namespace Legacy;\n\npublic class Old { }\n");
        File.WriteAllText(Path.Combine(dir, "Sdk", "Sdk.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(dir, "Sdk", "Class2.cs"), "namespace Sdk;\n\npublic class Modern { }\n");
        File.WriteAllText(Path.Combine(dir, "Broken", "Broken.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net48</TargetFramework>\n");

        using var helper = Helper48Process.Start(dir, solution);
        try
        {
            var reply = JsonDocument.Parse(helper.Send("{\"action\":\"loadSolution\",\"args\":{\"solutionPath\":" + JsonSerializer.Serialize(solution) + "}}"));
            var projects = reply.RootElement.GetProperty("result").GetProperty("result").GetProperty("projects");
            var byName = projects.EnumerateArray().ToDictionary(p => p.GetProperty("project").GetString()!, p => p);

            Assert.True(byName[Path.Combine("Legacy", "Legacy.csproj")].GetProperty("ok").GetBoolean());
            Assert.True(byName[Path.Combine("Sdk", "Sdk.csproj")].GetProperty("ok").GetBoolean());
            var broken = byName[Path.Combine("Broken", "Broken.csproj")];
            Assert.False(broken.GetProperty("ok").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(broken.GetProperty("error").GetString()));
        }
        finally
        {
            helper.Dispose();
        }
    }
}

/// <summary>Starts the real helper exe for a test and talks to its pipe.</summary>
internal sealed class Helper48Process : IDisposable
{
    private readonly Process _process;
    private readonly string _pipe;

    private Helper48Process(Process process, string pipe)
    {
        _process = process;
        _pipe = pipe;
    }

    public static Helper48Process Start(string folder, string solution)
    {
        var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RoslynHelper48.exe");
        var psi = new ProcessStartInfo(exe, $"--solution \"{solution}\" --manifest-folder \"{folder}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(psi)!;
        var manifest = Path.Combine(folder, process.Id + ".json");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(manifest))
        {
            if (DateTime.UtcNow > deadline)
            {
                process.Kill();
                throw new TimeoutException("The helper did not write its manifest in time.");
            }

            Thread.Sleep(100);
        }

        return new Helper48Process(process, "AiDevHelpers.Roslyn48." + process.Id);
    }

    public string Send(string line)
    {
        using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut);
        client.Connect(60000);
        var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine(line);
        var reader = new StreamReader(client, Encoding.UTF8);
        return reader.ReadLine() ?? throw new InvalidOperationException("The helper closed the pipe without a reply.");
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_process.HasExited)
        {
            _process.Kill();
            _process.WaitForExit(5000);
        }

        _process.Dispose();
    }
}
