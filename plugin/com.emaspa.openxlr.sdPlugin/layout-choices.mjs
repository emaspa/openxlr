// Pure helpers for the editable mixer layout. Targets always carry stable
// daemon ids; labels come from the latest state so renames do not invalidate
// existing OpenDeck actions.

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
  for (const mix of mixes) levelGroups.push({
    id: `layout-send-levels-${mix.id}`,
    label: `Sends: ${mix.name}`,
    items: channels.map((channel) => option(
      `send:${channel.id}:${mix.id}`, `${channel.name} in ${mix.name}`)),
  });
  return { toggleGroups, levelGroups };
}

// Presentation cannot supply arbitrary SVG markup or colour attributes.
export function targetAppearance(mixer, target) {
  if (typeof target !== "string") return {};
  let item;
  if (/^(mixmute|mixvol):/.test(target)) item = mixer?.mixes?.find(m => m.id === target.split(":")[1]);
  else if (/^(sendmute|send|focus):/.test(target)) item = mixer?.channels?.find(c => c.id === target.split(":")[1]);
  else if (/^(insert|inschain|insparam)\|/.test(target)) {
    const key = target.split("|")[1];
    item = key.startsWith("mix:") ? mixer?.mixes?.find(m => m.id === key.slice(4)) : mixer?.channels?.find(c => c.id === key);
  }
  const value = item?.appearance;
  return {
    icon: ["●", "♪", "♫", "✦", "◆", "▶", "◉"].includes(value?.icon) ? value.icon : "",
    colour: typeof value?.colour === "string" && /^#[0-9a-f]{6}$/i.test(value.colour) ? value.colour : null,
  };
}
