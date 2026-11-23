# Checks that a terminal opened through terminal_session is a real window on the user's desktop.
# Run it twice around the open: -Mode Snapshot before, -Mode Check after. A new terminal window passes
# when it is visible, has a size, belongs to the session this script runs in (the user's desktop, not session 0), and its elevation matches -Kind.
# An elevated window cannot be inspected by a standard caller; that denial counts as elevated.

#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Snapshot', 'Check')][string]$Mode,
    [Parameter(Mandatory)][string]$StateFile,
    [ValidateSet('Standard', 'Elevated')][string]$Kind = 'Standard'
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DesktopWindows {
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int length, out int returned);

    public sealed class Info {
        public long Handle; public uint Pid; public string Class = ""; public string Title = "";
        public bool Visible; public bool Minimized; public int Width; public int Height; public int Left; public int Top;
    }

    public static List<Info> Terminals() {
        var list = new List<Info>();
        EnumWindows((h, _) => {
            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            var name = cls.ToString();
            if (name != "CASCADIA_HOSTING_WINDOW_CLASS" && name != "ConsoleWindowClass") return true;
            var title = new StringBuilder(256);
            GetWindowText(h, title, 256);
            uint pid; GetWindowThreadProcessId(h, out pid);
            RECT r; GetWindowRect(h, out r);
            list.Add(new Info {
                Handle = h.ToInt64(), Pid = pid, Class = name, Title = title.ToString(),
                Visible = IsWindowVisible(h), Minimized = IsIconic(h),
                Width = r.Right - r.Left, Height = r.Bottom - r.Top, Left = r.Left, Top = r.Top,
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // 1 = elevated, 0 = not elevated, -1 = the token could not be opened (access denied for a standard caller).
    public static int Elevation(uint pid) {
        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return -1;
        try {
            IntPtr token;
            if (!OpenProcessToken(process, 0x8, out token)) return -1;
            try {
                int elevated, returned;
                return GetTokenInformation(token, 20, out elevated, 4, out returned) ? (elevated != 0 ? 1 : 0) : -1;
            } finally { CloseHandle(token); }
        } finally { CloseHandle(process); }
    }
}
'@

if ($Mode -eq 'Snapshot') {
    $handles = @([DesktopWindows]::Terminals() | ForEach-Object { $_.Handle })
    ConvertTo-Json -InputObject $handles -Compress | Set-Content -Path $StateFile -Encoding utf8
    Write-Host "snapshot: $($handles.Count) existing terminal window(s)"
    exit 0
}

$before = @(Get-Content -Path $StateFile -Raw | ConvertFrom-Json)
$new = @([DesktopWindows]::Terminals() | Where-Object { $_.Handle -notin $before })
# The user's desktop is the session this script runs in. It is not always the console session (a remote session has its own id).
$desktopSession = (Get-Process -Id $PID).SessionId
$callerElevated = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$wantElevated = $Kind -eq 'Elevated'

$failures = [System.Collections.Generic.List[string]]::new()
function Assert-Check([bool]$ok, [string]$name) {
    if ($ok) { Write-Host "PASS  $name" } else { Write-Host "FAIL  $name"; $script:failures.Add($name) }
}

Assert-Check ($new.Count -gt 0) "$Kind terminal: a new terminal window exists"
foreach ($window in $new) {
    $session = (Get-Process -Id $window.Pid).SessionId
    $level = [DesktopWindows]::Elevation($window.Pid)
    $isElevated = if ($level -eq -1) { -not $callerElevated } else { $level -eq 1 }
    Write-Host "window $($window.Handle) pid $($window.Pid) class $($window.Class) title '$($window.Title)' size $($window.Width)x$($window.Height) at $($window.Left),$($window.Top) session $session elevated $isElevated"
    Assert-Check $window.Visible "$Kind terminal: window is visible"
    Assert-Check (-not $window.Minimized -and $window.Width -gt 0 -and $window.Height -gt 0) "$Kind terminal: window has a size and is not minimized"
    Assert-Check ($session -eq $desktopSession -and $session -ne 0) "$Kind terminal: window is on the user's desktop session ($desktopSession)"
    Assert-Check ($isElevated -eq $wantElevated) "$Kind terminal: elevation is $($wantElevated.ToString().ToLower())"
}

exit ($failures.Count -gt 0 ? 1 : 0)
