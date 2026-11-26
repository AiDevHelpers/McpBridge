import { describe, expect, test } from "vitest";
import { EventHub, EventSubscriber, refusedSubscribeReply, subscribeReply } from "./eventHub";

function parse(line: string | undefined): { event: string; seq: number; data: unknown } {
    if (line === undefined) throw new Error("expected a line");
    return JSON.parse(line) as { event: string; seq: number; data: unknown };
}

describe("EventHub", () => {
    test("publish_namedFilter_deliversOnlyNamedEvents", () => {
        const hub = new EventHub();
        const sub = hub.subscribe(["buildDone"]);

        hub.publish("documentSaved", { path: "a.ts" });
        hub.publish("buildDone", { ok: true });

        const line = parse(sub.next());
        expect(line.event).toBe("buildDone");
        expect(sub.next()).toBeUndefined();
    });

    test("subscribe_emptyNames_getsEveryEvent", () => {
        const hub = new EventHub();
        const sub = hub.subscribe([]);

        hub.publish("documentSaved", null);
        hub.publish("buildBegin", null);

        expect(parse(sub.next()).event).toBe("documentSaved");
        expect(parse(sub.next()).event).toBe("buildBegin");
    });

    test("next_firstEvent_hasSeqOne", () => {
        const hub = new EventHub();
        const sub = hub.subscribe(undefined);

        hub.publish("documentOpened", { path: "a.ts" });
        hub.publish("documentOpened", { path: "b.ts" });

        expect(parse(sub.next()).seq).toBe(1);
        expect(parse(sub.next()).seq).toBe(2);
    });

    test("publish_beyondCapacity_dropsOldestAndSendsCountBeforeNextEvent", () => {
        const hub = new EventHub();
        const sub = hub.subscribe(undefined);

        for (let i = 0; i < 501; i++) {
            hub.publish("documentSaved", { i });
        }

        // The first event (seq 1, i 0) was dropped to make room. The dropped line takes the next sequence number, as in
        // EventSubscriber.Next() in the Visual Studio bridge.
        const dropped = parse(sub.next());
        expect(dropped).toEqual({ event: "dropped", seq: 502, data: { count: 1 } });

        const next = parse(sub.next());
        expect(next.seq).toBe(2);
        expect(next.data).toEqual({ i: 1 });
    });

    test("heartbeat_afterEvents_takesNextSeq", () => {
        const hub = new EventHub();
        const sub = hub.subscribe(undefined);

        hub.publish("buildBegin", null);
        sub.next();

        expect(parse(sub.heartbeat())).toEqual({ event: "heartbeat", seq: 2, data: null });
    });

    test("closeAll_closedSubscriberIgnoresLaterEvents", () => {
        const hub = new EventHub();
        const sub = hub.subscribe(undefined);

        hub.closeAll("bridgeStopped");
        hub.publish("buildDone", null);

        expect(sub.isClosed).toBe(true);
        expect(sub.closeReason).toBe("bridgeStopped");
        expect(sub.next()).toBeUndefined();
        expect(hub.count).toBe(0);
    });

    test("subscribe_wakesWriterOnPublish", () => {
        const hub = new EventHub();
        let wakes = 0;
        hub.subscribe(undefined, () => wakes++);

        hub.publish("buildDone", null);

        expect(wakes).toBe(1);
    });
});

describe("subscribe reply", () => {
    test("subscribeReply_ok_hasPidAndSubscriptionId", () => {
        const sub = new EventSubscriber(undefined);

        expect(JSON.parse(subscribeReply(42, sub.id))).toEqual({ ok: true, pid: 42, subscriptionId: sub.id });
        expect(sub.id.length).toBeGreaterThan(0);
    });

    test("refusedSubscribeReply_ok_false_hasError", () => {
        expect(JSON.parse(refusedSubscribeReply(42, "Expected a subscribe request."))).toEqual({
            ok: false,
            pid: 42,
            error: "Expected a subscribe request.",
        });
    });
});
