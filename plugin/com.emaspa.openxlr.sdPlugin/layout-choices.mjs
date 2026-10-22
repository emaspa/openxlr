// Pure helpers for the editable mixer layout. Targets always carry stable
// daemon ids; labels come from the latest state so renames do not invalidate
// existing OpenDeck actions.
import fs from "node:fs";

// The built-in icon set, generated from LayoutIcons.cs by tools/deck-icons.py.
const ICON_TABLE = JSON.parse(fs.readFileSync(new URL("./icons.json", import.meta.url), "utf8"));

const LEGACY_CHANNELS = {
  xlr1: "XLR 1", xlr2: "XLR 2", aux: "Aux In", game: "Game",
  music: "Music", browser: "Browser", system: "System",
  voicechat: "Voice Chat", sfx: "SFX",
};
const LEGACY_MIXES = { monitor: "Monitor A", monitor2: "Monitor B", stream: "Stream", chat: "Chat", auxout: "Aux" };

export function channelName(mixer, id) {
  return mixer?.channels?.find((channel) => channel.id === id)?.name
    ?? LEGACY_CHANNELS[id] ?? id;
}

export function mixName(mixer, id) {
  return mixer?.mixes?.find((mix) => mix.id === id)?.name
    ?? LEGACY_MIXES[id] ?? id;
}

// The explicit default survives reordering. Older daemons use their first monitor.
export function defaultMonitorMix(mixer) {
  const monitors = (mixer?.mixes ?? []).filter(mix => mix.kind === "monitor"
    || mix.kind == null && (mix.id === "monitor" || mix.id === "monitor2"));
  return monitors.find(mix => mix.id === mixer?.defaultMonitorMix)?.id
    ?? monitors[0]?.id ?? "monitor";
}

// Match the daemon's live feed: discard removed ids and use its implicit
// monitor only when no surviving member remains. Labels never identify a mix.
export function monitorFeed(mixer, output) {
  const feeds = mixer?.monitorFeeds ?? {};
  const saved = Object.hasOwn(feeds, output) ? feeds[output] : "";
  const parts = new Set((typeof saved === "string" ? saved : "").split("+").map(id => id.trim()).filter(Boolean));
  const ids = (mixer?.mixes ?? Object.keys(LEGACY_MIXES).map(id => ({ id })))
    .filter(mix => parts.has(mix.id)).map(mix => mix.id);
  return ids.length ? ids.join("+") : defaultMonitorMix(mixer);
}

export function mixShortName(mixer, id) {
  if (id === "all") return "All";
  const name = mixName(mixer, id);
  if (id === "monitor" && name === "Monitor A") return "MonA";
  if (id === "monitor2" && name === "Monitor B") return "MonB";
  return name.length <= 5 ? name : `${name.slice(0, 4)}…`;
}

const option = (target, label) => ({ target, label });

export function controllableOutputs(mixer, devices = []) {
  const monitors = new Set((mixer?.mixes ?? []).filter(m => m.kind === "monitor").map(m => `OpenXLR_mix_${m.id}`));
  return devices.filter(d => d.kind === 0 && d.name && d.name.length <= 256 && !/[\p{Cc}#]/u.test(d.name)
    && !/^[@-]|^[0-9]+$/.test(d.name) && (!d.isOwn || monitors.has(d.name)));
}

export function outputKey(target) {
  if (typeof target !== "string") return null;
  const prefixes = {"outputup:":"up", "outputdown:":"down", "outputmute:":"mute", "mainoutput:":"main"};
  for (const [prefix, kind] of Object.entries(prefixes))
    if (target.startsWith(prefix)) return {kind, device:target.slice(prefix.length) || null};
  return null;
}

export function layoutChoices(mixer, devices = []) {
  const mixes = mixer?.mixes ?? [];
  const channels = mixer?.channels ?? [];
  if (!mixes.length || !channels.length) return { toggleGroups: [], levelGroups: [] };

  const toggleGroups = [{
    id: "layout-mix-mutes",
    label: "Mix mutes",
    items: mixes.map((mix) => option(`mixmute:${mix.id}`, `${mix.name} mix mute`)),
  }];
  toggleGroups.push({
    id: "layout-focus", label: "Route focused application",
    items: channels.filter((channel) => !channel.hardware && !channel.captureSource)
      .map((channel) => option(`focus:${channel.id}`, `Focused app to ${channel.name}`)),
  });
  for (const mix of mixes) toggleGroups.push({
    id: `layout-send-mutes-${mix.id}`,
    label: `Send mutes: ${mix.name}`,
    items: channels.map((channel) => option(
      `sendmute:${channel.id}:${mix.id}`, `${channel.name} in ${mix.name}`)),
  });
  if (mixer.exclusiveGroups?.length) toggleGroups.push({
    id: "layout-exclusive-groups", label: "Exclusive groups",
    items: mixer.exclusiveGroups.map((group) => option(`group:${group.id}`, `${group.name}: next member`)),
  });

  const outputs = [{name:"", description:"Current system default"}, ...controllableOutputs(mixer, devices)];
  toggleGroups.push({id:"layout-output-keys", label:"System output controls", items:outputs.flatMap(d => [
    option(`outputup:${d.name}`, `${d.description || d.name}: louder 5%`),
    option(`outputdown:${d.name}`, `${d.description || d.name}: quieter 5%`),
    option(`outputmute:${d.name}`, `${d.description || d.name}: toggle mute`),
  ])});
  toggleGroups.push({id:"layout-main-output", label:"Enforced system output", items:[
    option("mainoutput:@monitor", "Follow selected monitor output"),
    ...outputs.filter(d => d.name).map(d => option(`mainoutput:${d.name}`, d.description || d.name)),
  ]});

  const levelGroups = [
    {
      id: "layout-mix-levels",
      label: "Mix masters",
      items: mixes.map((mix) => option(`mixvol:${mix.id}`, `${mix.name} mix master`)),
    },
    {
      id: "layout-output-levels",
      label: "Outputs",
      items: outputs.map((d) => option(`output:${d.name}`, d.description || d.name)),
    },
    {
      id: "layout-all-sends",
      label: "Sends: all mixes",
      items: channels.map((channel) => option(`send:${channel.id}:all`, `${channel.name} in all mixes`)),
    },
  ];
  // A channel volume is reported for every channel, XLR 1, XLR 2 and Aux In
  // included, by a daemon that has them; an older daemon reports it for
  // application and capture channels only, or not at all, and the group
  // lists what it reports. The label is the strip's name, so a bound XLR 1
  // reads "Mic 1 volume".
  const volumes = channels.filter((channel) => typeof channel.volume === "number");
  if (volumes.length) levelGroups.splice(2, 0, {
    id: "layout-channel-volumes",
    label: "Channel volumes",
    items: volumes.map((channel) => option(`chvol:${channel.id}`, `${channel.name} volume`)),
  });
  const microphones = channels.filter(channel => channel.unitInput);
  if (microphones.length) {
    toggleGroups.push({id:"layout-microphone-controls", label:"Microphone controls", items:microphones.flatMap(channel => [
      option(`chmute:${channel.id}`, `${channel.name} mute`),
      option(`chphantom:${channel.id}`, `${channel.name} phantom power`),
      option(`chlowcut:${channel.id}`, `${channel.name} low cut`),
      option(`chclipguard:${channel.id}`, `${channel.name} ClipGuard`),
    ])});
    levelGroups.push({id:"layout-microphone-gains", label:"Microphone controls", items:microphones.map(channel =>
      option(`chgain:${channel.id}`, `${channel.name} gain`))});
  }
  for (const mix of mixes) levelGroups.push({
    id: `layout-send-levels-${mix.id}`,
    label: `Sends: ${mix.name}`,
    items: channels.map((channel) => option(
      `send:${channel.id}:${mix.id}`, `${channel.name} in ${mix.name}`)),
  });
  return { toggleGroups, levelGroups };
}

// The member of an exclusive group heard in some mix, by channel id, or null.
export function openGroupMember(mixer, group) {
  return group?.channels?.find((id) => {
    const ch = mixer?.channels?.find((c) => c.id === id);
    return ch && Object.keys(ch.levels ?? {}).some((m) => !ch.mutedIn?.includes(m));
  }) ?? null;
}

// The icon and colour a channel or mix was given in the layout editor, for
// the key or dial whose target names it; a group key takes its open member's.
// Only a listed glyph, a built-in icon's own path data and a #RRGGBB colour
// come through, so state can never put markup or an attribute into the key's
// SVG. An empty icon or a name the plugin does not know (a skin's own icon)
// draws the default for the item's kind; "none" draws no icon.
const APPEARANCE_ICONS = ICON_TABLE.glyphs;

export function iconPath(name) {
  return typeof name === "string" && Object.hasOwn(ICON_TABLE.icons, name) ? ICON_TABLE.icons[name].path : null;
}

export function targetAppearance(mixer, target) {
  if (typeof target !== "string") return { icon: "", path: null, colour: null };
  let item;
  if (/^(mixmute|mixvol):/.test(target)) item = mixer?.mixes?.find(m => m.id === target.split(":")[1]);
  else if (/^(sendmute|send|focus|chvol|chgain|chmute|chphantom|chlowcut|chclipguard):/.test(target)) item = mixer?.channels?.find(c => c.id === target.split(":")[1]);
  else if (target.startsWith("group:")) {
    const open = openGroupMember(mixer, mixer?.exclusiveGroups?.find(g => g.id === target.slice(6)));
    item = open === null ? undefined : mixer?.channels?.find(c => c.id === open);
  }
  else if (/^(insert|inschain|insparam)\|/.test(target)) {
    const key = target.split("|")[1] ?? "";
    item = key.startsWith("mix:") ? mixer?.mixes?.find(m => m.id === key.slice(4)) : mixer?.channels?.find(c => c.id === key);
  }
  const value = item?.appearance;
  const glyph = APPEARANCE_ICONS.includes(value?.icon);
  const path = value?.icon === "none" || glyph ? null : iconPath(value?.icon) ?? iconPath(value?.defaultIcon);
  return {
    icon: glyph ? value.icon : "",
    path,
    colour: typeof value?.colour === "string" && /^#[0-9a-f]{6}$/i.test(value.colour) ? value.colour : null,
  };
}
