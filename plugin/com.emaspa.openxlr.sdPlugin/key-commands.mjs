import { randomUUID } from "node:crypto";

// Preserve press order across volume and mute keys, including at a volume
// limit where reordering up/down steps changes the result. Focus and output
// selection are not deferred to a time when their target may have changed.
export class KeyCommands {
  constructor(send, notify, {id = randomUUID, delay = setTimeout, cancel = clearTimeout} = {}) {
    this.send = send;
    this.notify = notify;
    this.id = id;
    this.delay = delay;
    this.cancel = cancel;
    this.active = null;
    this.waiting = [];
  }

  enqueue(context, payload) {
    if ((this.active && !["adjustOutputVolume", "toggleOutputMute"].includes(payload.cmd)) ||
        this.waiting.length + (this.active ? 1 : 0) >= 64 ||
        this.waiting.filter(entry => entry.context === context).length >= 8) {
      this.notify(context, true);
      return;
    }
    this.waiting.push({context, payload: {...payload}});
    this.advance();
  }

  advance() {
    if (this.active || this.waiting.length === 0) return;
    const entry = this.waiting.shift();
    const requestId = this.id();
    entry.requestId = requestId;
    this.active = entry;
    entry.timer = this.delay(() => this.finish(requestId, true), 8000);
    entry.timer.unref?.();
    if (!this.send({...entry.payload, requestId})) this.finish(requestId, true);
  }

  finish(requestId, failed) {
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
    for (const context of contexts) this.notify(context, true);
  }
}
