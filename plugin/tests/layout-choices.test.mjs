import assert from "node:assert/strict";
import test from "node:test";

import { channelName, layoutChoices, mixName } from "../com.emaspa.openxlr.sdPlugin/layout-choices.mjs";

const mixer = {
  mixes: [
    { id: "monitor", name: "My Ears" },
    { id: "broadcast-vod", name: "Broadcast + VOD" },
  ],
  channels: [
    { id: "xlr1", name: "Host Mic" },
    { id: "alerts-new", name: "Alerts & SFX" },
  ],
};

test("editable names are paired with stable ids", () => {
  const choices = layoutChoices(mixer);
  assert.deepEqual(choices.toggleGroups[0].items, [
    { target: "mixmute:monitor", label: "My Ears mix mute" },
    { target: "mixmute:broadcast-vod", label: "Broadcast + VOD mix mute" },
  ]);
  assert.ok(choices.toggleGroups.some((group) => group.items.some((item) =>
    item.target === "sendmute:alerts-new:broadcast-vod" && item.label === "Alerts & SFX in Broadcast + VOD")));
  assert.ok(choices.levelGroups.some((group) => group.items.some((item) =>
    item.target === "send:alerts-new:broadcast-vod")));
  assert.equal(channelName(mixer, "alerts-new"), "Alerts & SFX");
  assert.equal(mixName(mixer, "broadcast-vod"), "Broadcast + VOD");
});

test("deleted layout entries disappear from new choices", () => {
  const afterDelete = {
    mixes: mixer.mixes.filter((entry) => entry.id !== "broadcast-vod"),
    channels: mixer.channels.filter((entry) => entry.id !== "alerts-new"),
  };
  assert.doesNotMatch(JSON.stringify(layoutChoices(afterDelete)), /broadcast-vod|alerts-new/);
});
