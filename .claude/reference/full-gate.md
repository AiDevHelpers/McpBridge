# Full gate

Run before a PR to `dev` or `main`. One command per line in the `gate` block; a line that starts with `!` is advisory
(it may fail without stopping the gate). The gate stops at the first failing step.

Close running `AiDevHelpers.MCP` processes first: they lock the server's output files and fail the build.

```gate
! dotnet list AiDevHelpers.slnx package --outdated
pwsh -NoProfile -File scripts/gate-build.ps1
dotnet test AiDevHelpers.slnx -c Release --no-build
npm --prefix src/Installers/VsCode/Core/AiDevTools.VsCode.Core ci
npm --prefix src/Installers/VsCode/Core/AiDevTools.VsCode.Core run lint
npm --prefix src/Installers/VsCode/Core/AiDevTools.VsCode.Core test
npm --prefix src/Installers/VsCode/Installer/AiDevTools.VsCode ci
npm --prefix src/Installers/VsCode/Installer/AiDevTools.VsCode run build
npm --prefix src/Installers/VsCode/Installer/AiDevTools.VsCode run lint
npm --prefix src/Installers/VsCode/Installer/AiDevTools.VsCode test
```
