// Pure helpers for the editable mixer layout. Targets always carry stable
// daemon ids; labels come from the latest state so renames do not invalidate
// existing OpenDeck actions.

const LEGACY_CHANNELS = {
  xlr1: "XLR 1", xlr2: "XLR 2", aux: "Aux In", game: "Game",
  music: "Music", browser: "Browser", system: "System",
  voicechat: "Voice Chat", sfx: "SFX",
};
const LEGACY_MIXES = { monitor: "Monitor", stream: "Stream", chat: "Chat", auxout: "Aux" };

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
  return name.length <= 5 ? name : `${name.slice(0, 4)}…`;
}

const option = (target, label) => ({ target, label });

export function layoutChoices(mixer) {
  const mixes = mixer?.mixes ?? [];
  const channels = mixer?.channels ?? [];
  if (!mixes.length || !channels.length) return { toggleGroups: [], levelGroups: [] };

  const toggleGroups = [{
    id: "layout-mix-mutes",
    label: "Mix mutes",
    items: mixes.map((mix) => option(`mixmute:${mix.id}`, `${mix.name} mix mute`)),
  }];
  for (const mix of mixes) toggleGroups.push({
    id: `layout-send-mutes-${mix.id}`,
    label: `Send mutes: ${mix.name}`,
    items: channels.map((channel) => option(
      `sendmute:${channel.id}:${mix.id}`, `${channel.name} in ${mix.name}`)),
  });

  const levelGroups = [
    {
      id: "layout-mix-levels",
      label: "Mix masters",
      items: mixes.map((mix) => option(`mixvol:${mix.id}`, `${mix.name} mix master`)),
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
