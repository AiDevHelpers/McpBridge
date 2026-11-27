// Copies the vendored Core into dist so the packaged extension can load it at runtime.
import { cpSync, rmSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const from = join(here, "..", "src", "vendor", "core");
const to = join(here, "..", "dist", "vendor", "core");
rmSync(to, { recursive: true, force: true });
cpSync(from, to, { recursive: true });
console.log(`Copied vendored Core to ${to}`);

// The published Roslyn helper (scripts/vendor-roslyn.mjs) ships beside the extension, where roslynHelper.ts looks for it.
const roslynFrom = join(here, "..", "src", "roslyn");
const roslynTo = join(here, "..", "dist", "roslyn");
rmSync(roslynTo, { recursive: true, force: true });
cpSync(roslynFrom, roslynTo, { recursive: true });
console.log(`Copied the Roslyn helper to ${roslynTo}`);
