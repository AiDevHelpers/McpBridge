import * as vscode from "vscode";
import { t } from "./strings.js";

// Breakpoint changes for the enableBreakpoint, disableBreakpoint and setBreakpointCondition actions. VS Code
// breakpoints are immutable (enabled, condition and hitCondition are readonly), so a change removes each matching
// breakpoint and adds a replacement at the same location. Without confirm nothing changes; the reply only lists
// what would change.

export type HitCountType = "always" | "equal" | "multiple" | "greaterOrEqual";

export interface ConditionChange {
    condition?: string;
    hitCountType?: HitCountType;
    hitCount?: number;
}

interface Planned {
    breakpoint: vscode.SourceBreakpoint;
    next: vscode.SourceBreakpoint;
    entry: Record<string, unknown>;
}

const HIT_COUNT_TYPES: readonly string[] = ["always", "equal", "multiple", "greaterOrEqual"];

export function parseHitCountType(value: string | undefined): HitCountType | undefined {
    if (value === undefined) {
        return undefined;
    }
    if (!HIT_COUNT_TYPES.includes(value)) {
        throw new Error(t("unknownHitCountType", value));
    }
    return value as HitCountType;
}

// Maps a hit count type and count to the VS Code hitCondition string. "always" has no hitCondition.
export function hitConditionFor(type: HitCountType | undefined, count: number | undefined): string | undefined {
    if (type === undefined || type === "always") {
        return undefined;
    }
    if (count === undefined) {
        throw new Error(t("hitCountNeedsCount", type));
    }
    switch (type) {
        case "equal":
            return `== ${count}`;
        case "multiple":
            return `% ${count}`;
        case "greaterOrEqual":
            return `>= ${count}`;
    }
}

// Reads a VS Code hitCondition back into a type and count. Strings this bridge does not write come back with no type.
export function parseHitCondition(raw: string | undefined): { type: HitCountType | undefined; count: number | undefined } {
    if (raw === undefined || raw === "") {
        return { type: "always", count: undefined };
    }
    const match = /^(==|%|>=) (\d+)$/.exec(raw.trim());
    if (!match) {
        return { type: undefined, count: undefined };
    }
    const type = match[1] === "==" ? "equal" : match[1] === "%" ? "multiple" : "greaterOrEqual";
    return { type, count: Number(match[2]) };
}

function findBreakpoints(file: string, line: number): vscode.SourceBreakpoint[] {
    const matches = vscode.debug.breakpoints.filter(
        (b): b is vscode.SourceBreakpoint =>
            b instanceof vscode.SourceBreakpoint &&
            b.location.uri.fsPath === file &&
            b.location.range.start.line + 1 === line,
    );
    if (matches.length === 0) {
        throw new Error(t("noBreakpointAt", file, line));
    }
    return matches;
}

function replaceBreakpoints(planned: Planned[]): void {
    vscode.debug.removeBreakpoints(planned.map((p) => p.breakpoint));
    vscode.debug.addBreakpoints(planned.map((p) => p.next));
}

export function setBreakpointEnabled(file: string, line: number, enabled: boolean, confirm: boolean): unknown {
    const planned: Planned[] = findBreakpoints(file, line).map((breakpoint) => ({
        breakpoint,
        next: new vscode.SourceBreakpoint(breakpoint.location, enabled, breakpoint.condition, breakpoint.hitCondition, breakpoint.logMessage),
        entry: { file, line, enabled },
    }));
    const entries = planned.map((p) => p.entry);
    if (!confirm) {
        return { planned: true, would: entries };
    }
    replaceBreakpoints(planned);
    return { planned: false, applied: entries };
}

export function setBreakpointCondition(file: string, line: number, change: ConditionChange, confirm: boolean): unknown {
    if (change.condition === undefined && change.hitCountType === undefined && change.hitCount === undefined) {
        throw new Error(t("conditionNeedsField"));
    }
    const matches = findBreakpoints(file, line);

    const planned: Planned[] = matches.map((breakpoint) => {
        const proposedCondition = change.condition === undefined ? breakpoint.condition : change.condition === "" ? undefined : change.condition;
        const current = parseHitCondition(breakpoint.hitCondition);

        let proposedHitCondition = breakpoint.hitCondition;
        let proposedType = current.type;
        let proposedCount = current.count;
        if (change.hitCountType !== undefined || change.hitCount !== undefined) {
            const type = change.hitCountType ?? current.type;
            if (type === undefined) {
                throw new Error(t("hitCountTypeUnreadable"));
            }
            if (type === "always" && change.hitCount !== undefined) {
                throw new Error(t("hitCountNeedsType"));
            }
            const count = change.hitCount ?? current.count;
            proposedHitCondition = hitConditionFor(type, count);
            proposedType = type;
            proposedCount = type === "always" ? undefined : count;
        }

        return {
            breakpoint,
            next: new vscode.SourceBreakpoint(breakpoint.location, breakpoint.enabled, proposedCondition, proposedHitCondition, breakpoint.logMessage),
            entry: {
                file,
                line,
                condition: { current: breakpoint.condition ?? null, proposed: proposedCondition ?? null },
                hit_count_type: { current: current.type ?? null, proposed: proposedType ?? null },
                hit_count: { current: current.count ?? null, proposed: proposedCount ?? null },
            },
        };
    });

    const entries = planned.map((p) => p.entry);
    if (!confirm) {
        return { planned: true, would: entries };
    }
    replaceBreakpoints(planned);
    return { planned: false, applied: entries };
}
