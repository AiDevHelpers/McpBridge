import { randomUUID } from "node:crypto";

// Pure event fan-out for the events pipe (conformance/protocol.md, "Events pipe"). No vscode import: the installer
// maps VS Code events onto publish() calls. Mirrors src/Bridge/src/EventHub.cs.

/** Events held per subscriber before the oldest are dropped. */
export const EVENT_CAPACITY = 500;

/** One subscribed client: a name filter, a bounded queue of encoded lines, and a sequence counter. */
export class EventSubscriber {
    /** The subscription id sent to the client. */
    readonly id: string;

    private readonly names: ReadonlySet<string> | undefined;
    private readonly queue: string[] = [];
    private dropped = 0;
    private seq = 0;
    private closed = false;
    private reason: string | undefined;

    /**
     * @param names Event names wanted; undefined or empty means all events.
     * @param onWake Called after an event is queued or the subscriber is closed, so the writer can flush.
     */
    constructor(names: readonly string[] | undefined, private readonly onWake?: () => void) {
        this.id = randomUUID().replace(/-/g, "");
        this.names = names === undefined || names.length === 0 ? undefined : new Set(names);
    }

    /** True once the subscriber has been closed. */
    get isClosed(): boolean {
        return this.closed;
    }

    /** Why the subscriber was closed, or undefined while it is open. */
    get closeReason(): string | undefined {
        return this.reason;
    }

    /** Queues one event, dropping the oldest when the queue is full. Ignored when closed or filtered out. */
    offer(name: string, data: unknown): void {
        if (this.closed) return;
        if (this.names !== undefined && !this.names.has(name)) return;
        if (this.queue.length >= EVENT_CAPACITY) {
            this.queue.shift();
            this.dropped++;
        }
        this.queue.push(encode(name, ++this.seq, data));
        this.onWake?.();
    }

    /** Writer side: the next encoded line (a pending "dropped" line first), or undefined when nothing is queued. */
    next(): string | undefined {
        if (this.dropped > 0) {
            const count = this.dropped;
            this.dropped = 0;
            return encode("dropped", ++this.seq, { count });
        }
        return this.queue.shift();
    }

    /** Writer side: a heartbeat line, numbered like the other events. */
    heartbeat(): string {
        return encode("heartbeat", ++this.seq, null);
    }

    /** Marks the subscriber closed with the reason and wakes the writer. A second close does nothing. */
    close(reason: string): void {
        if (this.closed) return;
        this.closed = true;
        this.reason = reason;
        this.onWake?.();
    }
}

/** Holds the open subscribers and fans each published event out to those that want its name. */
export class EventHub {
    private readonly subscribers = new Set<EventSubscriber>();

    /** Adds a subscriber for the given event names; undefined or empty means all events. */
    subscribe(names?: readonly string[], onWake?: () => void): EventSubscriber {
        const subscriber = new EventSubscriber(names, onWake);
        this.subscribers.add(subscriber);
        return subscriber;
    }

    /** Closes the subscriber with the reason and removes it from the hub. */
    unsubscribe(subscriber: EventSubscriber, reason: string): void {
        subscriber.close(reason);
        this.subscribers.delete(subscriber);
    }

    /** Offers one event to every subscriber. */
    publish(name: string, data?: unknown): void {
        for (const subscriber of [...this.subscribers]) {
            subscriber.offer(name, data);
        }
    }

    /** Closes every subscriber with the reason and clears the hub. */
    closeAll(reason: string): void {
        const snapshot = [...this.subscribers];
        this.subscribers.clear();
        for (const subscriber of snapshot) {
            subscriber.close(reason);
        }
    }

    /** The number of current subscribers. */
    get count(): number {
        return this.subscribers.size;
    }
}

/** The subscribe reply line: {"ok":true,"pid":N,"subscriptionId":"..."}. */
export function subscribeReply(pid: number, subscriptionId: string): string {
    return JSON.stringify({ ok: true, pid, subscriptionId });
}

/** The reply to a refused subscribe: {"ok":false,"pid":N,"error":"..."}. */
export function refusedSubscribeReply(pid: number, error: string): string {
    return JSON.stringify({ ok: false, pid, error });
}

function encode(name: string, seq: number, data: unknown): string {
    return JSON.stringify({ event: name, seq, data: data ?? null });
}
