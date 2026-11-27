// Spike A (throwaway, docs/plans/2026-11-26-tray-service.md). Runs in the console user's session when the spike service starts it.
// Writes what it can see to %ProgramData%\AiDevHelpers\spike-a.txt and serves the same text on 127.0.0.1:5057/healthz.
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

var report = new StringBuilder();
var identity = WindowsIdentity.GetCurrent();
report.AppendLine($"time={DateTime.UtcNow:O}");
report.AppendLine($"pid={Environment.ProcessId} session={Process.GetCurrentProcess().SessionId} user={identity.Name}");
report.AppendLine($"administratorRole={new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)}");
var pipes = Directory.GetFiles(@"\\.\pipe\", "AiDevHelpers.VS.*").Select(Path.GetFileName).ToArray();
report.AppendLine($"idePipes={string.Join(",", pipes)}");
if (pipes.Length > 0)
{
    try
    {
        using var client = new NamedPipeClientStream(".", pipes[0]!, PipeDirection.InOut);
        client.Connect(3000);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
        writer.WriteLine("{\"action\":\"health\"}");
        report.AppendLine($"ideHealth={reader.ReadLine()}");
    }
    catch (Exception ex)
    {
        report.AppendLine($"ideHealthError={ex.GetType().Name}: {ex.Message}");
    }
}

var dataDir = Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "AiDevHelpers");
Directory.CreateDirectory(dataDir);
File.WriteAllText(Path.Combine(dataDir, "spike-a.txt"), report.ToString());

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5057");
var app = builder.Build();
app.MapGet("/healthz", () => report.ToString());
await app.RunAsync();
