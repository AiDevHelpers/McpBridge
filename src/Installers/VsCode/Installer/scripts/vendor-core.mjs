// Copies the built Core library into src/vendor/core so the extension packages without a file: link.
import { cpSync, rmSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { execSync } from "node:child_process";

const here = dirname(fileURLToPath(import.meta.url));
const core = join(here, "..", "..", "Core");
execSync("npm run build", { cwd: core, stdio: "inherit" });
const dest = join(here, "..", "src", "vendor", "core");
rmSync(dest, { recursive: true, force: true });
mkdirSync(dest, { recursive: true });
cpSync(join(core, "dist"), dest, { recursive: true });
console.log(`Vendored Core into ${dest}`);
