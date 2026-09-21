import assert from "node:assert/strict";
import test from "node:test";
import { KeyCommands } from "../com.emaspa.openxlr.sdPlugin/key-commands.mjs";

function harness() {
  const sent = [], notices = [], timers = new Map();
  let sequence = 0, online = true;
  const keys = new KeyCommands(payload => { if (!online) return false; sent.push(payload); return true; },
    (context, failed) => notices.push({context, failed}), {
      id: () => String(++sequence),
      delay: (callback, ms) => { assert.equal(ms, 8000); const timer = {callback}; timers.set(timer, callback); return timer; },
      cancel: timer => timers.delete(timer),
    });
  return {keys, sent, notices, timers, offline: () => { online = false; },
    ack: (failed = false) => keys.finish(sent.at(-1).requestId, failed),
    expire: () => [...timers.values()][0]()};
}
const up = {cmd:"adjustOutputVolume", device:"headphones", value:.05};
const down = {...up, value:-.05};

test("volume steps retain press order across keys, including at the 150 percent limit", () => {
  const h = harness();
  h.keys.enqueue("up", up);
  h.keys.enqueue("up", up);
  h.keys.enqueue("down", down);
  assert.equal(h.sent.length, 1);
  let volume = 1.5;
  for (let i = 0; i < 3; i++) {
    volume = Math.max(0, Math.min(1.5, volume + h.sent.at(-1).value));
    h.ack();
  }
  assert.deepEqual(h.sent.map(p => p.value), [.05, .05, -.05]);
  assert.equal(volume, 1.45);
  assert.equal(h.timers.size, 0);
});

test("per-key overflow is visible and the accepted queue remains bounded", () => {
  const h = harness();
  for (let i = 0; i < 20; i++) h.keys.enqueue("a", up);
  assert.equal(h.sent.length, 1);
  assert.equal(h.notices.filter(n => n.failed).length, 11);
  for (let i = 0; i < 9; i++) h.ack();
  assert.equal(h.sent.length, 9);
  assert.equal(h.timers.size, 0);
});

test("the global budget includes queued commands and is released after completion", () => {
  const h = harness();
  for (let i = 0; i < 8; i++) for (let j = 0; j < 8; j++) h.keys.enqueue(String(i), up);
  assert.equal(h.sent.length, 1);
  h.keys.enqueue("extra", up);
  assert.deepEqual(h.notices.at(-1), {context:"extra", failed:true});
  h.keys.finish(h.sent[0].requestId, false);
  h.keys.enqueue("extra", up);
  assert.equal(h.sent.length, 2);
  assert.equal(h.notices.filter(n => n.context === "extra" && n.failed).length, 1);
  while (h.timers.size) h.ack();
  assert.equal(h.sent.length, 65);
});

test("removing a key cancels only its actions and releases the next key", () => {
  const h = harness();
  h.keys.enqueue("a", up);
  const stale = h.sent[0].requestId;
  h.keys.enqueue("a", up);
  h.keys.enqueue("b", down);
  h.keys.clear("a");
  assert.equal(h.sent.length, 2);
  assert.equal(h.sent[1].value, -.05);
  h.keys.finish(stale, true);
  assert.equal(h.timers.size, 1);
  h.ack();
  assert.equal(h.sent.length, 2);
  assert.equal(h.timers.size, 0);
});

for (const reason of ["error", "timeout", "disconnect", "clear", "send failure"]) {
  test(`${reason} discards queued actions and late replies cannot affect a new press`, () => {
    const h = harness();
    h.keys.enqueue("a", up);
    const stale = h.sent[0].requestId;
    h.keys.enqueue("a", down);
    if (reason === "error") h.ack(true);
    else if (reason === "timeout") h.expire();
    else if (reason === "disconnect") h.keys.disconnect();
    else if (reason === "clear") h.keys.clear("a");
    else { h.offline(); h.ack(); }
    assert.equal(h.sent.length, 1);
    assert.equal(h.timers.size, 0);
    const notices = h.notices.length;
    h.keys.finish(stale, false);
    h.keys.finish(stale, true);
    assert.equal(h.notices.length, notices);
    if (reason !== "send failure") {
      h.keys.enqueue("a", down);
      h.keys.finish(stale, false);
      assert.equal(h.sent.length, 2);
      assert.equal(h.timers.size, 1);
      h.ack();
      assert.equal(h.timers.size, 0);
    }
  });
}

for (const payload of [{cmd:"routeFocusedApp", channel:"music"}, {cmd:"setMainOutput", device:"headphones"}]) {
  test(`${payload.cmd} is not deferred to a later application or output`, () => {
    const h = harness();
    h.keys.enqueue("action", payload);
    h.keys.enqueue("action", payload);
    assert.deepEqual(h.notices.at(-1), {context:"action", failed:true});
    h.ack();
    assert.equal(h.sent.length, 1);
  });
}

test("toggle output mute retains every accepted toggle", () => {
  const h = harness();
  for (let i = 0; i < 3; i++) h.keys.enqueue("mute", {cmd:"toggleOutputMute", device:null});
  for (let i = 0; i < 3; i++) h.ack();
  assert.equal(h.sent.length, 3);
  assert.equal(h.timers.size, 0);
});
