using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper48.Tests;

// Starts the real RoslynHelper48.exe and talks to it over its named pipe, as the bridge does.
public class Helper48PipeTests
{
    [Fact]
    public void health_over_the_pipe_reports_framework_net48_and_the_manifest_says_so()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rh48-" + Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RoslynHelper48.exe");
        var psi = new ProcessStartInfo(exe, $"--solution \"{Path.Combine(folder, "App.slnx")}\" --manifest-folder \"{folder}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var helper = Process.Start(psi)!;
        try
        {
            var manifestPath = Path.Combine(folder, helper.Id + ".json");
            WaitUntil(() => File.Exists(manifestPath));

            var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
            var pipe = "AiDevHelpers.Roslyn48." + helper.Id;
            Assert.Equal("net48", manifest.GetProperty("framework").GetString());
            Assert.Equal(pipe, manifest.GetProperty("pipeName").GetString());

            using var reply = JsonDocument.Parse(SendLine(pipe, "{\"action\":\"health\"}"));
            Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
            // The bridge answers health itself; the framework is reported by the manifest, which the routing reads.
            Assert.Equal(helper.Id, reply.RootElement.GetProperty("pid").GetInt32());
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill();
                helper.WaitForExit(5000);
            }
        }
    }

    private static string SendLine(string pipeName, string line)
    {
        // The client stream is disposed once, at the end; a writer or reader that disposes it first would close the pipe early.
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        client.Connect(10000);
        var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine(line);
        var reader = new StreamReader(client, Encoding.UTF8);
        return reader.ReadLine() ?? throw new InvalidOperationException("The helper closed the pipe without a reply.");
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The helper did not write its manifest in time.");
            }

            Thread.Sleep(100);
        }
    }
}
