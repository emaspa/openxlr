// The undo and redo keys: Toggle keys that send undo or redo to the daemon's
// one history, which the UI and the terminal mixer share, so a key takes back
// the newest change whoever made it. The key names the step it would take
// from the state's history, never lights, and is grey with nothing on its
// side or from a daemon that keeps no history, so a press there only alerts.

export const isHistoryTarget = (target) => target === "undo" || target === "redo";

// The step the key would take, or null.
export const historyStep = (state, target) => state?.history?.[target] ?? null;

// Dark but usable while its side holds a step; grey (null) otherwise.
export const historyValue = (state, target) => historyStep(state, target) ? false : null;

// The command a press sends. It carries the id of the step the key showed,
// so the daemon refuses it when another client changed the history first.
export function historyPress(step, target) {
  const payload = {cmd: target};
  if (Number.isSafeInteger(step?.step) && step.step > 0) payload.step = step.step;
  return payload;
}

// The step's label cut to the whole words that fit one key line, so
// "XLR 1 level in Monitor A" reads "XLR 1 level…".
const LINE = 12;
export function shortStep(label) {
  const text = String(label ?? "").trim().replace(/\s+/g, " ");
  if (text.length <= LINE) return text;
  let out = "";
  for (const word of text.split(" ")) {
    const next = out ? `${out} ${word}` : word;
    if (next.length > LINE - 1) break;
    out = next;
  }
  return `${out || text.slice(0, LINE - 1)}…`;
}

// The key's built-in label: the verb over the step it would take.
export function historyLabel(step, target) {
  const verb = target === "redo" ? "Redo" : "Undo";
  const what = shortStep(step?.label);
  return what ? `${verb}\n${what}` : verb;
}
