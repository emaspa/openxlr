// Shared property-inspector glue: registers with the host (Elgato-compatible
// entry point, which OpenDeck calls too), loads the action's settings into
// the #target select, and saves on change. It also asks the plugin for the
// list of physical output devices and appends them as a "Monitor output"
// group, so a key can switch the monitor mix to a specific device.
//
// The key's target is only ever changed by a choice in the target list. The
// list may not hold the saved target (the daemon is offline, or its output,
// insert or profile is gone), and then it reads empty or shows the
// "Unavailable" entry; a change of icon or press mode keeps the target.
"use strict";

let ws = null, piUuid = null, actionContext = null;

function connect(inPort, inPropertyInspectorUUID, inRegisterEvent, inInfo, inActionInfo) {
  piUuid = inPropertyInspectorUUID;
  const actionInfo = JSON.parse(inActionInfo);
  actionContext = actionInfo.context;
  const settings = actionInfo.payload?.settings ?? {};
  let wanted = typeof settings.target === "string" ? settings.target : "";

  ws = new WebSocket("ws://localhost:" + inPort);
  ws.onopen = () => {
    ws.send(JSON.stringify({ event: inRegisterEvent, uuid: piUuid }));
    const sel = document.getElementById("target");
    const iconSel = document.getElementById("icon");
    // A voice changer key's press mode, on the toggle page only.
    const modeSel = document.getElementById("voiceMode");
    const modeRow = document.getElementById("voice-mode-row");
    const showMode = () => { if (modeRow) modeRow.hidden = !wanted.startsWith("voice:"); };
    selectTarget(sel, wanted);
    if (iconSel && settings.icon) iconSel.value = settings.icon;
    if (modeSel) modeSel.value = settings.voiceMode === "hold" ? "hold" : "toggle";
    showMode();
    const save = () => {
      settings.target = wanted;
      if (iconSel) settings.icon = iconSel.value;
      if (modeSel) settings.voiceMode = modeSel.value;
      showMode();
      // Insert options carry {plugin, index} so the key survives a chain
      // rebuild that hands the insert a new id.
      const opt = sel.selectedOptions[0];
      if (opt?.dataset.meta && opt.value === wanted) {
        settings.meta = settings.meta ?? {};
        settings.meta[wanted] = JSON.parse(opt.dataset.meta);
      }
      ws.send(JSON.stringify({ event: "setSettings", context: piUuid, payload: settings }));
    };
    // A choice in the list is the only way the target changes; it also drops
    // the entry of a target that was unavailable.
    sel.addEventListener("change", () => { wanted = sel.value; selectTarget(sel, wanted); save(); });
    iconSel?.addEventListener("change", save);
    modeSel?.addEventListener("change", save);
    // Ask the plugin for the live output-device list, the insert chains and
    // the saved profiles.
    for (const request of ["layout", "outputs", "inserts", "profiles"])
      ws.send(JSON.stringify({ event: "sendToPlugin", context: actionContext, payload: { request } }));
  };

  ws.onmessage = (e) => {
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    if (m.event !== "sendToPropertyInspector") return;
    if (Array.isArray(m.payload?.toggleGroups)) replaceLayoutOptions(m.payload.toggleGroups, wanted);
    if (Array.isArray(m.payload?.outputs)) fillMonitors(m.payload.outputs, wanted);
    if (Array.isArray(m.payload?.inserts)) {
      fillGroup("insert-group", "Insert bypass", m.payload.inserts, wanted);
      fillGroup("chain-group", "Insert chain bypass (all plugins)", m.payload.chains ?? [], wanted);
    }
    if (Array.isArray(m.payload?.profiles))
      fillGroup("profile-group", "Profiles (recall)", m.payload.profiles, wanted);
  };
}

// A group of live options {target, label, meta?}. The plugin sends it again
// whenever it changes, so it is rebuilt, keeping the key's target selected.
function fillGroup(id, title, items, wanted) {
  const sel = document.getElementById("target");
  if (sel) replaceOptionGroup(sel, id, title, items, wanted);
}

// The plugin sends the outputs again whenever they change, so both groups
// are rebuilt, keeping the current choice.
function fillMonitors(outputs, wanted) {
  const sel = document.getElementById("target");
  if (!sel) return;
  document.getElementById("monitor-group")?.remove();
  document.getElementById("feed-group")?.remove();
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
  // The same outputs again as feed switches: the key steps the output's
  // feed through Monitor A, Monitor B, Monitor A+B and then every other
  // mix (only meaningful once it is ticked as a monitor output in the UI).
  const feeds = document.createElement("optgroup");
  feeds.id = "feed-group";
  feeds.label = "Monitor feed (cycles A, B, A+B, then every other mix)";
  for (const o of outputs) {
    const opt = document.createElement("option");
    opt.value = "feed:" + o.name;
    opt.textContent = (o.description || o.name) + ": next feed";
    feeds.appendChild(opt);
  }
  sel.appendChild(feeds);
  // A previously saved monitor target may not have existed as an <option>
  // until now, so re-apply the selection once the group is present.
  selectTarget(sel, wanted);
}

window.connectElgatoStreamDeckSocket = connect;
window.connectOpenActionSocket = connect;
