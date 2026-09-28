// The daemon owns restoration. Lost key-up, Deck removal and disconnects
// stop renewal; no held state is replayed onto a replacement connection.
export class MomentaryEffects {
  constructor(send, uuid, alert, timers = { start: fn => setInterval(fn, 1500), stop: clearInterval }) {
    this.send = send; this.uuid = uuid; this.alert = alert; this.timers = timers;
    this.held = new Map(); this.timer = null;
  }
  begin(context, channel, insertId) {
    if (this.held.has(context)) return true;
    if (this.held.size >= 128) { this.alert(context); return false; }
    const holdId = this.uuid().replaceAll("-", "");
    if (!this.send({cmd:"holdInsert", action:"begin", channel, insertId, holdId, requestId:holdId})) {
      this.alert(context); return false;
    }
    this.held.set(context, {holdId});
    if (this.timer === null) {
      this.timer = this.timers.start(() => this.renew());
      this.timer?.unref?.();
    }
    return true;
  }
  end(context, send = true) {
    const hold = this.held.get(context);
    if (!hold) return;
    this.held.delete(context);
    if (send) this.send({cmd:"holdInsert", action:"end", holdId:hold.holdId});
    if (!this.held.size && this.timer !== null) { this.timers.stop(this.timer); this.timer = null; }
  }
  renew() {
    for (const [context, hold] of this.held)
      if (!this.send({cmd:"holdInsert", action:"renew", holdId:hold.holdId})) this.end(context, false);
  }
  reply(id, error) {
    if (!error) return;
    for (const [context, hold] of this.held)
      if (hold.holdId === id) { this.end(context); this.alert(context); break; }
  }
  clear(send = false) { for (const context of [...this.held.keys()]) this.end(context, send); }
}
