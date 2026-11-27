using AiDevHelpers.MCP.Http;

// Entry point. Arguments: --port <n> (default 5757), --token <file>, --endpoint <file>.
// Files default to %LOCALAPPDATA%\AiDevHelpers\mcp-http\ of the user this process runs as.
var port = McpHttpHost.DefaultPort;
var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiDevHelpers", "mcp-http");
var tokenPath = Path.Combine(folder, "token");
var endpointPath = Path.Combine(folder, "endpoint.json");
for (var i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--port":
            port = int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            break;
        case "--token":
            tokenPath = args[i + 1];
            break;
        case "--endpoint":
            endpointPath = args[i + 1];
            break;
    }
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

try
{
    await using var host = await McpHttpHost.StartAsync(new HttpHostOptions(port, tokenPath, endpointPath), stop.Token);
    await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
}
catch (PortInUseException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (OperationCanceledException)
{
}

return 0;
