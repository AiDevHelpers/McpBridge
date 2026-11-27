using System.Text;
using AiDevHelpers.Common;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// The elevated .NET workload installer. The script carries <see cref="InstallableWorkloads"/> as its table and the table's hash as its pin.
/// Before it does anything it hashes its own table and refuses when the hash differs from the pin, and it refuses any name not in the table.
/// The caller passes a name only, never an id.
/// </summary>
public static class InstallWorkloadFiles
{
    public const string ScriptName = "install-workload.ps1";

    public static string Script()
    {
        var table = InstallableWorkloads.TableText().TrimEnd('\n');
        return $$"""
            #Requires -Version 7
            # Installs one .NET workload with dotnet workload install. The name must be in the table below; the table's hash must equal the pin.
            param([Parameter(Mandatory)][string]$Workload)
            $ErrorActionPreference = 'Stop'
            $pin = '{{InstallableWorkloads.TableHash()}}'
            $table = @'
            {{table}}
            '@
            $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($table + "`n")))
            if ($actual -ne $pin) { [Console]::Error.WriteLine('workload table does not match its pin'); exit 4 }
            $names = $table -split "`n" | Where-Object { $_ }
            if (-not ($names -contains $Workload)) { [Console]::Error.WriteLine("unknown workload '$Workload'"); exit 2 }
            if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) { [Console]::Error.WriteLine('dotnet is not on PATH'); exit 3 }
            & dotnet.exe workload install $Workload
            exit $LASTEXITCODE

            """.Replace("\r\n", "\n");
    }

    /// <summary>Writes the script into the tools folder, next to the tool installer.</summary>
    public static void Write(string toolsRoot)
    {
        Directory.CreateDirectory(toolsRoot);
        File.WriteAllText(Path.Combine(toolsRoot, ScriptName), Script(), new UTF8Encoding(false));
    }
}
