// Shared property-inspector glue: registers with the host (Elgato-compatible
// entry point, which OpenDeck calls too), loads the action's settings into
// the #target select, and saves on change. It also asks the plugin for the
// list of physical output devices and appends them as a "Monitor output"
// group, so a key can switch the monitor mix to a specific device.
"use strict";

let ws = null, piUuid = null, actionContext = null;

function connect(inPort, inPropertyInspectorUUID, inRegisterEvent, inInfo, inActionInfo) {
  piUuid = inPropertyInspectorUUID;
  const actionInfo = JSON.parse(inActionInfo);
  actionContext = actionInfo.context;
  const settings = actionInfo.payload?.settings ?? {};
  const wanted = settings.target;

  ws = new WebSocket("ws://localhost:" + inPort);
  ws.onopen = () => {
    ws.send(JSON.stringify({ event: inRegisterEvent, uuid: piUuid }));
    const sel = document.getElementById("target");
    const iconSel = document.getElementById("icon");
    if (wanted) sel.value = wanted;
    if (iconSel && settings.icon) iconSel.value = settings.icon;
    const save = () => {
      settings.target = sel.value;
      if (iconSel) settings.icon = iconSel.value;
      // Insert options carry {plugin, index} so the key survives a chain
      // rebuild that hands the insert a new id.
      const opt = sel.selectedOptions[0];
      if (opt?.dataset.meta) {
        settings.meta = settings.meta ?? {};
        settings.meta[sel.value] = JSON.parse(opt.dataset.meta);
      }
      ws.send(JSON.stringify({ event: "setSettings", context: piUuid, payload: settings }));
    };
    sel.addEventListener("change", save);
    iconSel?.addEventListener("change", save);
    // Ask the plugin for the live output-device list, the insert chains and
    // the saved profiles.
    for (const request of ["outputs", "inserts", "profiles", "layout"])
      ws.send(JSON.stringify({ event: "sendToPlugin", context: actionContext, payload: { request } }));
  };

  ws.onmessage = (e) => {
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    if (m.event !== "sendToPropertyInspector") return;
    if (Array.isArray(m.payload?.outputs)) fillMonitors(m.payload.outputs, wanted);
    if (Array.isArray(m.payload?.inserts)) {
      fillGroup("insert-group", "Insert bypass", m.payload.inserts, wanted);
      fillGroup("chain-group", "Insert chain bypass (all plugins)", m.payload.chains ?? [], wanted);
    }
    if (Array.isArray(m.payload?.profiles))
      fillGroup("profile-group", "Profiles (recall)", m.payload.profiles, wanted);
    if (Array.isArray(m.payload?.toggleGroups))
      for (const group of m.payload.toggleGroups)
        fillGroup(group.id, group.label, group.items ?? [], wanted);
  };
}

// A group of live options {target, label, meta?}; re-applies the saved
// selection once the option it refers to exists.
function fillGroup(id, title, items, wanted) {
  const sel = document.getElementById("target");
  if (!sel || document.getElementById(id) || !items.length) return;
  const group = document.createElement("optgroup");
  group.id = id;
  group.label = title;
  for (const it of items) {
    const opt = document.createElement("option");
    opt.value = it.target;
    opt.textContent = it.label;
    if (it.meta) opt.dataset.meta = JSON.stringify(it.meta);
    group.appendChild(opt);
  }
  sel.appendChild(group);
  if (wanted) sel.value = wanted;
}

function fillMonitors(outputs, wanted) {
  const sel = document.getElementById("target");
  if (!sel || document.getElementById("monitor-group")) return;
  const group = document.createElement("optgroup");
  group.id = "monitor-group";
  group.label = "Monitor output";
  for (const o of outputs) {
    const opt = document.createElement("option");
    opt.value = "monitor:" + o.name;
    opt.textContent = o.description || o.name;
    group.appendChild(opt);
  }
  sel.appendChild(group);
  // A previously saved monitor target may not have existed as an <option>
  // until now, so re-apply the selection once the group is present.
  if (wanted) sel.value = wanted;
}

window.connectElgatoStreamDeckSocket = connect;
window.connectOpenActionSocket = connect;
