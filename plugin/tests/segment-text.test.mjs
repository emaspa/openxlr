import test from 'node:test';
import assert from 'node:assert/strict';
import { segmented, segmentText, segmentWidth, fittedSize } from '../com.emaspa.openxlr.sdPlugin/segment-text.mjs';

test('only a ghost colour with alpha turns the keys into a display', () => {
  assert.equal(segmented('#00000000'), false);
  assert.equal(segmented(undefined), false);
  assert.equal(segmented('#281308'), true);
  assert.equal(segmented('#281308ff'), true);
});

test('every whole cell is one width, the point narrower, and a missing character keeps a blank cell', () => {
  const cell = segmentWidth('8', 1000);
  assert.equal(segmentWidth('A', 1000), cell);
  assert.equal(segmentWidth(' ', 1000), cell);
  assert.equal(segmentWidth('…', 1000), cell);
  assert.ok(segmentWidth('.', 1000) > 0 && segmentWidth('.', 1000) < cell);
  assert.equal(segmentWidth('·', 1000), cell);
  assert.equal(segmentWidth('XLR 1', 20), 5 * cell * 20 / 1000);
});

test('text shrinks to fit and never grows past its size', () => {
  assert.equal(fittedSize('AB', 20, 1000), 20);
  const size = fittedSize('VOICE CHANGER', 20, 120);
  assert.ok(size < 20);
  assert.ok(Math.abs(segmentWidth('VOICE CHANGER', size) - 120) < 0.01);
});

test('unlit cells stand behind whole cells only, under the lit lettering', () => {
  const svg = segmentText('A.1 B', 72, 100, 20, { fill: '#ff9634', ghost: '#281308' });
  const ghost = svg.match(/<g fill="#281308">(.*?)<\/g>/)[1];
  // A, 1, the space and B have cells; the point has none.
  assert.equal(ghost.match(/<path /g).length, 4);
  assert.ok(svg.indexOf('fill="#281308"') < svg.indexOf('fill="#ff9634"'));
  // No ghost colour, no cells.
  assert.ok(!segmentText('AB', 0, 0, 20, { fill: '#fff' }).includes('<g fill="null"'));
  assert.equal((segmentText('AB', 0, 0, 20, { fill: '#fff' }).match(/<g /g) || []).length, 1);
});

test('anchors place the run at its start, middle or end', () => {
  const at = (anchor) => Number(segmentText('A', 100, 0, 10, { fill: '#fff', anchor }).match(/translate\(([-\d.]+)/)[1]);
  const width = segmentWidth('A', 10);
  assert.equal(at('start'), 100);
  assert.ok(Math.abs(at('middle') - (100 - width / 2)) < 0.01);
  assert.ok(Math.abs(at('end') - (100 - width)) < 0.01);
});
