import test from 'node:test';
import assert from 'node:assert/strict';
import {MomentaryEffects} from '../com.emaspa.openxlr.sdPlugin/momentary-effects.mjs';
function setup() {
  let next = 0, starts = 0, stops = 0;
  const sent = [], alerts = [];
  const holds = new MomentaryEffects(o => {sent.push(o); return true;}, () => String(++next).padStart(32, '0'), c => alerts.push(c),
    {start: () => {starts++; return 1;}, stop: () => stops++});
  return {holds, sent, alerts, counts: () => ({starts, stops})};
}
test('one begin per press, renewal while held and one end per release', () => {
  const {holds, sent, counts} = setup();
  holds.begin('key', 'xlr1', 'effect'); holds.begin('key', 'xlr1', 'effect');
  assert.equal(sent.length, 1);
  holds.renew(); holds.end('key'); holds.end('key'); holds.renew();
  assert.deepEqual(sent.map(s => s.action), ['begin', 'renew', 'end']);
  assert.ok(sent.every(s => s.holdId === sent[0].holdId));
  assert.deepEqual(counts(), {starts:1, stops:1});
});
test('release before begin acknowledgement and late errors cannot release a new press', () => {
  const {holds, sent, alerts} = setup();
  holds.begin('key', 'xlr1', 'effect');
  const old = sent[0].requestId;
  holds.end('key'); holds.begin('key', 'xlr1', 'effect');
  holds.reply(old, 'late error');
  assert.equal(holds.held.size, 1); assert.deepEqual(alerts, []);
  holds.reply(sent[2].requestId, 'refused');
  assert.equal(holds.held.size, 0); assert.deepEqual(alerts, ['key']);
});
test('disconnect drops renewals without replaying onto a new daemon', () => {
  const {holds, sent, counts} = setup();
  holds.begin('a', 'xlr1', 'one'); holds.begin('b', 'mix:monitor');
  holds.clear(); holds.renew(); holds.end('a');
  assert.equal(sent.length, 2); assert.deepEqual(counts(), {starts:1, stops:1});
  holds.begin('a', 'xlr1', 'one'); assert.notEqual(sent[0].holdId, sent[2].holdId);
});
test('failed send and failed renewal leave no running timer', () => {
  const {holds, sent, alerts, counts} = setup();
  holds.send = () => false;
  assert.equal(holds.begin('a', 'xlr1'), false); assert.equal(holds.held.size, 0);
  assert.deepEqual(alerts, ['a']); assert.deepEqual(counts(), {starts:0, stops:0});
  holds.send = o => {sent.push(o); return true;}; holds.begin('b', 'xlr1');
  holds.send = () => false; holds.renew(); assert.equal(holds.held.size, 0);
  assert.deepEqual(counts(), {starts:1, stops:1});
});
test('removal ends every hold and enforces a bounded number of keys', () => {
  const {holds, sent, alerts} = setup();
  for (let i=0; i<128; i++) assert.equal(holds.begin(String(i), 'xlr1'), true);
  assert.equal(holds.begin('overflow', 'xlr1'), false);
  assert.deepEqual(alerts, ['overflow']); holds.clear(true);
  assert.equal(sent.filter(s => s.action === 'end').length, 128);
  assert.equal(holds.held.size, 0);
});
