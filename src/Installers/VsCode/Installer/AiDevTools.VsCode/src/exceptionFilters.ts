import { t } from "./strings.js";

// Exception filters of the active debug session, from the debug adapter's initialize reply (the DAP
// exceptionBreakpointFilters capability). VS Code's API does not expose the adapter's capabilities, so the bridge
// reads the initialize reply through its debug adapter tracker. This is the reduced model: only the DAP filters are
// listed and changed. Visual Studio's exception groups have no VS Code equivalent.

/** The group name the filters are listed under. DAP filters have no groups of their own. */
export const ADAPTER_GROUP = "Debug adapter";

export interface DapExceptionFilter {
    filter: string;
    label?: string;
    default?: boolean;
}

interface SessionFilters {
    filters: DapExceptionFilter[];
    enabled: Map<string, boolean>;
}

const bySession = new Map<string, SessionFilters>();

/** Records the filters from a DAP initialize response. Other messages are ignored. */
export function recordDapMessage(sessionId: string, message: unknown): void {
    const m = message as { type?: string; command?: string; success?: boolean; body?: { exceptionBreakpointFilters?: unknown } } | undefined;
    if (m?.type !== "response" || m.command !== "initialize" || m.success === false) {
        return;
    }
    const raw = m.body?.exceptionBreakpointFilters;
    if (!Array.isArray(raw)) {
        return;
    }
    const filters = (raw as DapExceptionFilter[]).filter((f) => typeof f?.filter === "string");
    bySession.set(sessionId, { filters, enabled: new Map(filters.map((f) => [f.filter, f.default === true])) });
}

export function forgetSession(sessionId: string): void {
    bySession.delete(sessionId);
}

/** listExceptions reply. Without a session, or without filters from its adapter, the list is empty and the note says why. */
export function shapeExceptions(sessionId: string | undefined, groupFilter: string | undefined): { exceptions: Array<Record<string, unknown>>; note?: string } {
    if (sessionId === undefined) {
        return { exceptions: [], note: t("noDebugSession") };
    }
    const known = bySession.get(sessionId);
    if (known === undefined) {
        return { exceptions: [], note: t("noExceptionFilters") };
    }
    if (groupFilter !== undefined && groupFilter !== ADAPTER_GROUP) {
        return { exceptions: [] };
    }
    return {
        exceptions: known.filters.map((f) => ({
            group: ADAPTER_GROUP,
            name: f.filter,
            label: f.label ?? f.filter,
            code: null,
            break_when_thrown: known.enabled.get(f.filter) === true,
            break_when_user_unhandled: null,
            user_defined: false,
        })),
    };
}

export interface ExceptionChange {
    group?: string;
    name: string;
    breakWhenThrown?: boolean;
    breakWhenUserUnhandled?: boolean;
}

/**
 * The change and the filter set it would send. Nothing is stored here: commitExceptionChange runs after the adapter accepts the request.
 * breakWhenUserUnhandled has no DAP filter in the reduced model, so it is reported as proposed but never sent.
 */
export function planExceptionChange(sessionId: string | undefined, change: ExceptionChange): { reply: Record<string, unknown>; filters: string[]; enabledAfter: Map<string, boolean> } {
    if (change.breakWhenThrown === undefined && change.breakWhenUserUnhandled === undefined) {
        throw new Error(t("exceptionFlagRequired"));
    }
    const known = sessionId === undefined ? undefined : bySession.get(sessionId);
    if (known === undefined) {
        throw new Error(t("noExceptionFilters"));
    }
    if (change.group !== undefined && change.group !== ADAPTER_GROUP) {
        throw new Error(t("noExceptionNamed", change.group, change.name));
    }
    if (!known.filters.some((f) => f.filter === change.name)) {
        throw new Error(t("noExceptionNamed", ADAPTER_GROUP, change.name));
    }
    const currentThrown = known.enabled.get(change.name) === true;
    const enabledAfter = new Map(known.enabled);
    if (change.breakWhenThrown !== undefined) {
        enabledAfter.set(change.name, change.breakWhenThrown);
    }
    const filters = known.filters.map((f) => f.filter).filter((id) => enabledAfter.get(id) === true);
    const reply = {
        group: ADAPTER_GROUP,
        name: change.name,
        break_when_thrown: { current: currentThrown, proposed: change.breakWhenThrown ?? currentThrown },
        break_when_user_unhandled: { current: null, proposed: change.breakWhenUserUnhandled ?? null },
    };
    return { reply, filters, enabledAfter };
}

/** Stores the enabled set after the adapter accepted the setExceptionBreakpoints request. */
export function commitExceptionChange(sessionId: string, enabledAfter: Map<string, boolean>): void {
    const known = bySession.get(sessionId);
    if (known !== undefined) {
        known.enabled = enabledAfter;
    }
}
