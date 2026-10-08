import { randomUUID } from "node:crypto";

// Output volume and mute presses go through one ordered queue, across keys,
// so a burst at a volume limit applies in the order it was pressed. Focus
// routing and output selection are sent at once beside the queue: holding
// them back would apply them later to whatever application or output is
// current by then.
const ORDERED = new Set(["adjustOutputVolume", "toggleOutputMute"]);
const OUTSTANDING_LIMIT = 64;
const WAITING_PER_KEY = 8;
const TIMEOUT_MS = 8000;

export class KeyCommands {
  constructor(send, notify, {id = randomUUID, delay = setTimeout, cancel = clearTimeout} = {}) {
    this.send = send;
    this.notify = notify;
    this.id = id;
    this.delay = delay;
    this.cancel = cancel;
    this.active = null;
    this.waiting = [];
    this.immediate = new Map();   // requestId -> {context, timer}
  }

  outstanding() {
    return this.waiting.length + (this.active ? 1 : 0) + this.immediate.size;
  }

  enqueue(context, payload) {
    if (!ORDERED.has(payload.cmd)) { this.direct(context, payload); return; }
    if (this.outstanding() >= OUTSTANDING_LIMIT ||
        this.waiting.filter(entry => entry.context === context).length >= WAITING_PER_KEY) {
      this.notify(context, true);
      return;
    }
    this.waiting.push({context, payload: {...payload}});
    this.advance();
  }

  // One unanswered command per key; a repeat press while it is out is ignored.
  direct(context, payload) {
    if ([...this.immediate.values()].some(entry => entry.context === context)) return;
    if (this.outstanding() >= OUTSTANDING_LIMIT) { this.notify(context, true); return; }
    const requestId = this.id();
    const entry = {context};
    this.immediate.set(requestId, entry);
    entry.timer = this.delay(() => this.finish(requestId, true), TIMEOUT_MS);
    entry.timer.unref?.();
    if (!this.send({...payload, requestId})) this.finish(requestId, true);
  }

  advance() {
    if (this.active || this.waiting.length === 0) return;
    const entry = this.waiting.shift();
    const requestId = this.id();
    entry.requestId = requestId;
    this.active = entry;
    entry.timer = this.delay(() => this.finish(requestId, true), TIMEOUT_MS);
    entry.timer.unref?.();
    if (!this.send({...entry.payload, requestId})) this.finish(requestId, true);
  }

  finish(requestId, failed) {
    const direct = this.immediate.get(requestId);
    if (direct) {
      this.cancel(direct.timer);
      this.immediate.delete(requestId);
      this.notify(direct.context, failed);
      return;
    }
    const entry = this.active;
    if (!entry || entry.requestId !== requestId) return;
    this.cancel(entry.timer);
    this.active = null;
    // A failed or timed-out command may already have changed the output.
    // Discard waiting presses instead of replaying an uncertain burst.
    const dropped = failed ? this.waiting.splice(0) : [];
    this.notify(entry.context, failed);
    for (const context of new Set(dropped.map(e => e.context)))
      if (context !== entry.context) this.notify(context, true);
    this.advance();
  }

  clear(context) {
    this.waiting = this.waiting.filter(entry => entry.context !== context);
    for (const [requestId, entry] of this.immediate)
      if (entry.context === context) { this.cancel(entry.timer); this.immediate.delete(requestId); }
    if (this.active?.context === context) {
      this.cancel(this.active.timer);
      this.active = null;
    }
    this.advance();
  }

  disconnect() {
    const contexts = new Set(this.waiting.map(entry => entry.context));
    this.waiting = [];
    if (this.active) {
      this.cancel(this.active.timer);
      contexts.add(this.active.context);
      this.active = null;
    }
    for (const entry of this.immediate.values()) {
      this.cancel(entry.timer);
      contexts.add(entry.context);
    }
    this.immediate.clear();
    for (const context of contexts) this.notify(context, true);
  }
}
