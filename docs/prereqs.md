# Prerequisites

Tools this repo expects on the machine. Install the .NET diagnostic tools once; the `dotnet_diag` MCP tool reports what is missing.

## .NET diagnostic tools

Install each as a global tool:

```powershell
dotnet tool install --global dotnet-dump
dotnet tool install --global dotnet-stack
dotnet tool install --global dotnet-counters
dotnet tool install --global dotnet-gcdump
dotnet tool install --global dotnet-trace
dotnet tool install --global dotnet-symbol
```

| Tool | Used for | `dotnet_diag` action |
| --- | --- | --- |
| dotnet-dump | Memory dumps and the process list | `processes`, `dump` |
| dotnet-stack | Managed stacks of every thread in a live process | `stack` |
| dotnet-counters | Live CPU, GC and exception counters | `counters` |
| dotnet-gcdump | GC heap snapshot of a live process | `gcdump` |
| dotnet-trace | Profiling trace of a running process | `trace` |
| dotnet-symbol | Symbols for a dump or binary | `symbols` |

Check what is installed: call `dotnet_diag` with `action: status`. It lists each tool with its path and version, or the install command for a missing one.
