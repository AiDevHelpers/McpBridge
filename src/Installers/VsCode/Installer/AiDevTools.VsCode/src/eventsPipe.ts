import * as net from "net";
import { EventHub, type EventSubscriber, refusedSubscribeReply, subscribeReply } from "./vendor/core/index.js";

// Events pipe for this VS Code window, matching the Visual Studio bridge (conformance/protocol.md, "Events pipe").
// A client connects, writes one subscribe line {"action":"subscribe","args":{"events":[...]}}, gets one reply line,
// then reads event lines until it closes the connection. Behaviour follows src/Bridge/src/EventServer.cs.

/** Silence after which a heartbeat line is sent. */
export const HEARTBEAT_MS = 15_000;
/** How long one write may stay blocked before the subscriber is closed as slow. */
export const WRITE_LIMIT_MS = 30_000;
/** How long a client may take to send its subscribe line. */
const SUBSCRIBE_LIMIT_MS = 10_000;
/** Largest subscribe line accepted. */
const MAX_LINE_CHARS = 64 * 1024;
/** Subscribers allowed at once, as on the Visual Studio events pipe. */
export const MAX_SUBSCRIBERS = 8;
const WATCHDOG_MS = 10_000;

/** The events pipe name for one extension host: AiDevHelpers.VsCode.<pid>.events. */
export function eventsPipeNameFor(pid: number): string {
    return `AiDevHelpers.VsCode.${pid}.events`;
}

/** One connection after its subscribe line: its subscriber and its timers. */
interface Connection {
    socket: net.Socket;
    subscriber: EventSubscriber;
    heartbeat?: NodeJS.Timeout;
    slow?: NodeJS.Timeout;
    blocked: boolean;
    finished: boolean;
}

/** Serves the events pipe. Publish goes through the hub; this class only frames lines and handles the socket. */
export class EventsPipe {
    private server: net.Server | undefined;
    private watchdog: NodeJS.Timeout | undefined;
    private readonly pipeName: string;
    private readonly connections = new Set<Connection>();

    constructor(
        private readonly hub: EventHub,
        private readonly pid: number,
    ) {
        this.pipeName = eventsPipeNameFor(pid);
    }

    /** Starts listening. A watchdog restarts the pipe if the listener stops. */
    start(): void {
        this.listen();
        this.watchdog = setInterval(() => {
            if (!this.server?.listening) this.listen();
        }, WATCHDOG_MS);
    }

    /** Closes every subscriber with reason "bridgeStopped" and stops listening. */
    dispose(): void {
        if (this.watchdog) clearInterval(this.watchdog);
        this.watchdog = undefined;
        this.hub.closeAll("bridgeStopped");
        this.server?.close();
        this.server = undefined;
    }

    private listen(): void {
        const server = net.createServer((socket) => this.handle(socket));
        server.on("error", (err) => {
            console.error(`AiDevHelpers events pipe error: ${err.message}`);
        });
        server.listen(`\\\\.\\pipe\\${this.pipeName}`);
        this.server = server;
    }

    private handle(socket: net.Socket): void {
        let buffer = "";
        let subscribed = false;
        const limit = setTimeout(() => {
            if (!subscribed) this.refuse(socket, "Subscribe line not received in time.");
        }, SUBSCRIBE_LIMIT_MS);

        socket.setEncoding("utf8");
        socket.on("data", (chunk: string) => {
            if (subscribed) return;
            buffer += chunk;
            const newline = buffer.indexOf("\n");
            if (newline < 0) {
                if (buffer.length > MAX_LINE_CHARS) {
                    clearTimeout(limit);
                    this.refuse(socket, "Subscribe line too long.");
                }
                return;
            }
            subscribed = true;
            clearTimeout(limit);
            this.subscribe(socket, buffer.slice(0, newline).replace(/\r$/, ""));
        });
        socket.on("error", () => {
            // The client went away; the close handler does the cleanup.
        });
        socket.on("close", () => {
            clearTimeout(limit);
            const connection = [...this.connections].find((c) => c.socket === socket);
            if (connection) this.finish(connection, connection.subscriber.closeReason ?? "unsubscribed");
        });
    }

    private subscribe(socket: net.Socket, line: string): void {
        let names: string[] | undefined;
        try {
            names = parseSubscribe(line);
        } catch (err) {
            this.refuse(socket, err instanceof Error ? err.message : String(err));
            return;
        }

        if (this.hub.count >= MAX_SUBSCRIBERS) {
            this.refuse(socket, "Too many subscribers.");
            return;
        }

        // The hub wakes the writer through the callback; the callback runs later, after this connection exists.
        const connection: Connection = {
            socket,
            subscriber: this.hub.subscribe(names, () => this.flush(connection)),
            blocked: false,
            finished: false,
        };
        this.connections.add(connection);
        this.write(connection, subscribeReply(this.pid, connection.subscriber.id));
    }

    // Sends queued lines, then the closed line once the subscriber is closed and drained. Stops while a write is blocked.
    private flush(connection: Connection): void {
        if (connection.finished || connection.blocked) return;
        const { subscriber } = connection;
        for (let line = subscriber.next(); line !== undefined; line = subscriber.next()) {
            if (!this.write(connection, line) || connection.blocked) return;
        }
        if (subscriber.isClosed) {
            const closed = JSON.stringify({ event: "closed", reason: subscriber.closeReason });
            connection.socket.write(closed + "\n");
            this.finish(connection, subscriber.closeReason ?? "unsubscribed");
            connection.socket.end();
        }
    }

    // Writes one line and re-arms the heartbeat. A write that stays blocked for WRITE_LIMIT_MS closes the subscriber as slow.
    private write(connection: Connection, line: string): boolean {
        if (connection.finished) return false;
        const ok = connection.socket.write(line + "\n");
        this.armHeartbeat(connection);
        if (!ok && !connection.blocked) {
            connection.blocked = true;
            connection.slow = setTimeout(() => {
                connection.subscriber.close("slowSubscriber");
                this.finish(connection, "slowSubscriber");
                connection.socket.destroy();
            }, WRITE_LIMIT_MS);
            connection.socket.once("drain", () => {
                clearTimeout(connection.slow);
                connection.blocked = false;
                this.flush(connection);
            });
        }
        return ok;
    }

    private armHeartbeat(connection: Connection): void {
        if (connection.heartbeat) clearTimeout(connection.heartbeat);
        connection.heartbeat = setTimeout(() => {
            if (connection.finished || connection.blocked) return;
            connection.socket.write(connection.subscriber.heartbeat() + "\n");
            this.armHeartbeat(connection);
        }, HEARTBEAT_MS);
    }

    private refuse(socket: net.Socket, error: string): void {
        socket.end(refusedSubscribeReply(this.pid, error) + "\n");
    }

    // Stops the timers and removes the subscriber from the hub. Safe to call more than once.
    private finish(connection: Connection, reason: string): void {
        if (connection.finished) return;
        connection.finished = true;
        if (connection.heartbeat) clearTimeout(connection.heartbeat);
        if (connection.slow) clearTimeout(connection.slow);
        this.connections.delete(connection);
        this.hub.unsubscribe(connection.subscriber, reason);
    }
}

// The subscribe line: {"action":"subscribe","args":{"events":["buildDone",...]}}. No events, or an empty list, means all events.
function parseSubscribe(line: string): string[] | undefined {
    let request: unknown;
    try {
        request = JSON.parse(line);
    } catch {
        throw new Error("Subscribe line is not JSON.");
    }
    const root = request as { action?: unknown; args?: { events?: unknown } } | null;
    if (root?.action !== "subscribe") {
        throw new Error("Expected a subscribe request.");
    }
    const events = root.args?.events;
    if (!Array.isArray(events)) return undefined;
    const names = events.filter((name): name is string => typeof name === "string" && name.length > 0);
    return names.length > 0 ? names : undefined;
}
