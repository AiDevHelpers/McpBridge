// Publishes the Roslyn helper into src/roslyn so the extension can ship it. copy-vendor.mjs copies it into dist/roslyn.
import { mkdirSync, rmSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { execSync } from "node:child_process";

const here = dirname(fileURLToPath(import.meta.url));
const project = join(here, "..", "..", "..", "..", "..", "Helpers", "RoslynHelper", "src", "AiDevHelpers.Helpers.RoslynHelper.csproj");
const dest = join(here, "..", "src", "roslyn");
rmSync(dest, { recursive: true, force: true });
mkdirSync(dest, { recursive: true });
execSync(`dotnet publish "${project}" -c Release -o "${dest}"`, { stdio: "inherit" });
console.log(`Published the Roslyn helper into ${dest}`);
