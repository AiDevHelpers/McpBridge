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
