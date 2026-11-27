// Runs the vendoring steps in order. One node entry point, so npm needs no shell operator: `&&` fails in Windows
// PowerShell and `;` fails in cmd.exe. Each imported script runs its top-level code when it loads.
await import("./vendor-core.mjs");
await import("./vendor-roslyn.mjs");
