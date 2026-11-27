// Publishes the Roslyn helper into src/roslyn so the extension can ship it. copy-vendor.mjs copies it into dist/roslyn.
import { mkdirSync, rmSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { execSync } from "node:child_process";

const here = dirname(fileURLToPath(import.meta.url));
// Both helpers: the net10 one (SDK-style solutions) and the net48 one (legacy and net48 projects, Framework MSBuild).
const helpers = [
    { project: join(here, "..", "..", "..", "..", "..", "Helpers", "RoslynHelper", "src", "AiDevHelpers.Helpers.RoslynHelper.csproj"), dest: join(here, "..", "src", "roslyn") },
    { project: join(here, "..", "..", "..", "..", "..", "Helpers", "RoslynHelper48", "src", "AiDevHelpers.Helpers.RoslynHelper48.csproj"), dest: join(here, "..", "src", "roslyn48") },
];
for (const { project, dest } of helpers) {
    rmSync(dest, { recursive: true, force: true });
    mkdirSync(dest, { recursive: true });
    execSync(`dotnet publish "${project}" -c Release -o "${dest}"`, { stdio: "inherit" });
    console.log(`Published a Roslyn helper into ${dest}`);
}
