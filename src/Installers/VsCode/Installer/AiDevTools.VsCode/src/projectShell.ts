import * as path from "path";
import { t } from "./strings.js";

// Pure parts of the project and shell actions (projectProperties, addProjectItem, removeProjectItem, getSetting,
// setStatus, showInfoBar, writeActivityLog). They take plain values, so the tests run without the vscode module.
// ideActions.ts does the file and editor work and calls these for the shape of each reply.

export type ItemMode = "fromFile" | "fromNew";
export type LogLevel = "Error" | "Warning" | "Information";

export interface AddItemChange {
    project: string;
    file: string;
    mode: ItemMode;
    template: string | null;
}

export interface RemoveItemChange {
    project: string;
    file: string;
    stays_on_disk: true;
}

const LOG_LEVELS: readonly LogLevel[] = ["Error", "Warning", "Information"];

function stripComments(xml: string): string {
    return xml.replace(/<!--[\s\S]*?-->/g, "");
}

// Reads the named properties from the csproj XML. Only unconditional PropertyGroup elements and unconditional
// properties count, and the last assignment wins, as MSBuild evaluates them. Imports, SDK defaults and
// Directory.Build.props are not read, so a property those set returns null here.
export function readProjectProperties(csprojText: string, names: readonly string[]): Record<string, string | null> {
    const values = new Map<string, string>();
    const groups = /<PropertyGroup\b([^>]*?)(?<!\/)>([\s\S]*?)<\/PropertyGroup>/g;
    const elements = /<([A-Za-z_][\w.-]*)(\s[^>]*?)?(?<!\/)>([^<]*)<\/\1>/g;
    for (const group of stripComments(csprojText).matchAll(groups)) {
        if (/\sCondition\s*=/.test(group[1])) {
            continue;
        }
        for (const element of group[2].matchAll(elements)) {
            if (element[2] !== undefined && /\sCondition\s*=/.test(element[2])) {
                continue;
            }
            values.set(element[1], element[3].trim());
        }
    }
    return Object.fromEntries(names.map((name) => [name, values.get(name) ?? null]));
}

// True when the csproj is SDK-style (<Project Sdk="...">). SDK-style projects include files by globbing.
export function isSdkStyle(csprojText: string): boolean {
    return /<Project\b[^>]*\sSdk\s*=/.test(stripComments(csprojText));
}

// True when target is inside dir (or dir itself is not required to exist).
export function isInsideDir(dir: string, target: string): boolean {
    const relative = path.relative(dir, target);
    return relative.length > 0 && !relative.startsWith("..") && !path.isAbsolute(relative);
}

// Validates an addProjectItem request and returns the change it describes. Order matches the C# version.
export function addItemChange(project: string, file: string, mode: string, template: string | undefined): AddItemChange {
    if (mode === "fromNew" && (template === undefined || template.length === 0)) {
        throw new Error(t("templateRequiredForNew"));
    }
    if (mode !== "fromFile" && mode !== "fromNew") {
        throw new Error(t("unknownItemMode", mode));
    }
    return { project, file, mode, template: template ?? null };
}

export function removeItemChange(project: string, file: string): RemoveItemChange {
    return { project, file, stays_on_disk: true };
}

// getSetting shape. A value of undefined means the setting is not defined: found is false and value is null.
export function settingReply(key: string, value: unknown): { key: string; found: boolean; value: unknown } {
    if (value === undefined) {
        return { key, found: false, value: null };
    }
    return { key, found: true, value };
}

// Labels of the info bar buttons. The actions argument is an array of { "label": "..." } objects; anything else gives none.
export function infoBarLabels(actions: unknown): string[] {
    if (!Array.isArray(actions)) {
        return [];
    }
    return actions.map((action) => {
        const label = (action as { label?: unknown } | null)?.label;
        if (typeof label !== "string" || label.length === 0) {
            throw new Error(t("missingArgument", "label"));
        }
        return label;
    });
}

export function logLevel(level: string): LogLevel {
    const found = LOG_LEVELS.find((known) => known === level);
    if (found === undefined) {
        throw new Error(t("unknownLogLevel", level));
    }
    return found;
}

// One activity-log line: ISO timestamp, level, source, message.
export function formatLogLine(at: Date, level: LogLevel, source: string, message: string): string {
    return `${at.toISOString()} [${level}] ${source}: ${message}`;
}
