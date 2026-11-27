// Reply shaping for the debugger inspection actions (listDebuggedProcesses, listThreads, listStackFrames, detachAll).
// Pure functions over plain data, so they can be tested without the vscode module. The field names and nesting match
// the Visual Studio bridge (IdeActions.cs ListDebuggedProcesses, ListThreads, ListStackFrames, DetachAll).

/** The Visual Studio dbgDebugMode names, so callers see the same value from either IDE. */
export type DebuggerMode = "dbgDesignMode" | "dbgRunMode" | "dbgBreakMode";

/** The parts of a VS Code debug session this module needs. */
export interface SessionInfo {
    name: string;
    request: string;
    processId?: number;
}

export interface ProcessEntry {
    pid: number | null;
    name: string;
}

export interface DapThread {
    id: number;
    name: string;
}

export interface DapFrame {
    name: string;
}

/** No session: design mode. A session that is not stopped: run mode. A session with a stopped stack item: break mode. */
export function debuggerMode(hasSession: boolean, hasStoppedThread: boolean): DebuggerMode {
    if (!hasSession) {
        return "dbgDesignMode";
    }
    return hasStoppedThread ? "dbgBreakMode" : "dbgRunMode";
}

function processEntry(session: SessionInfo): ProcessEntry {
    return { pid: session.processId ?? null, name: session.name };
}

/** An attached session is one the debugger connected to (request "attach"), not one it launched. */
export function isAttached(session: SessionInfo): boolean {
    return session.request === "attach";
}

export function attachedSessions(sessions: SessionInfo[]): SessionInfo[] {
    return sessions.filter(isAttached);
}

export function shapeProcesses(sessions: SessionInfo[]): { processes: ProcessEntry[] } {
    return { processes: sessions.map(processEntry) };
}

/** Threads come from the DAP "threads" reply. Outside break mode no thread list is read, so the result is empty. */
export function shapeThreads(
    mode: DebuggerMode,
    currentThreadId: number | undefined,
    reply: { threads?: DapThread[] } | undefined,
): { debugger_mode: DebuggerMode; current_thread_id: number | null; threads: DapThread[] } {
    if (mode !== "dbgBreakMode") {
        return { debugger_mode: mode, current_thread_id: null, threads: [] };
    }
    return {
        debugger_mode: mode,
        current_thread_id: currentThreadId ?? null,
        threads: (reply?.threads ?? []).map((thread) => ({ id: thread.id, name: thread.name })),
    };
}

/**
 * Frames come from the DAP "stackTrace" reply. DAP carries no return type, so return_type is null here;
 * the Visual Studio bridge fills it from the frame's ReturnType.
 */
export function shapeStackFrames(
    mode: DebuggerMode,
    reply: { stackFrames?: DapFrame[] } | undefined,
): { debugger_mode: DebuggerMode; frames: Array<{ index: number; function: string; return_type: null }> } {
    if (mode !== "dbgBreakMode") {
        return { debugger_mode: mode, frames: [] };
    }
    return {
        debugger_mode: mode,
        frames: (reply?.stackFrames ?? []).map((frame, index) => ({ index, function: frame.name, return_type: null })),
    };
}

/** Without confirm, nothing changes: the reply lists what would be detached. With confirm, the same list is reported as applied. */
export function shapeDetachAll(
    confirm: boolean,
    attached: SessionInfo[],
): { planned: true; would: { detached: ProcessEntry[] } } | { planned: false; applied: { detached: ProcessEntry[] } } {
    const detached = attached.map(processEntry);
    return confirm ? { planned: false, applied: { detached } } : { planned: true, would: { detached } };
}
