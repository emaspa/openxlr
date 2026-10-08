import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const layout = JSON.parse(readFileSync(new URL(
  "../com.emaspa.openxlr.sdPlugin/layouts/dial.json", import.meta.url), "utf8"));

test("dial elements fit the touch strip and peers do not overlap", () => {
  for (const item of layout.items) {
    const [x, y, width, height] = item.rect;
    assert.ok(x >= 0 && y >= 0 && width > 0 && height > 0, item.key);
    assert.ok(x + width <= 200 && y + height <= 100, item.key);
  }
  for (let i = 0; i < layout.items.length; i++) {
    const a = layout.items[i];
    for (const b of layout.items.slice(i + 1)) {
      if (a.zOrder !== b.zOrder) continue;
      const [ax, ay, aw, ah] = a.rect, [bx, by, bw, bh] = b.rect;
      assert.ok(ax + aw <= bx || bx + bw <= ax || ay + ah <= by || by + bh <= ay,
        `${a.key} overlaps ${b.key} at z-order ${a.zOrder}`);
    }
  }
});
