// The clip key: readers of the daemon's mixer.soundboard, with no I/O, so the
// drawing and the presses in plugin.mjs stay small and testable. A key stores
// its clip by id ("clip:<id>"), which a rename in the library never changes,
// or "stopClips" for a key that stops every clip.
import { appearanceOf, targetAppearance } from "./layout-choices.mjs";

export const CLIP_ACTION = "com.emaspa.openxlr.clip";
export const STOP_ALL = "stopClips";
export const MODES = ["overlap", "restart", "toggle", "hold"];

// A name short enough for one line of a key face, as unit-inputs.mjs does.
const keyLine = (name) => name.length <= 11 ? name : `${name.slice(0, 10).trimEnd()}…`;

export const clipIdOf = (target) =>
  typeof target === "string" && target.startsWith("clip:") && target.length > 5 ? target.slice(5) : null;

// Why the board cannot play now, as one word for a key, or null when it can.
// The board's error is null exactly when it can play; the word only names
// which of its reasons applies, and the inspector shows the sentence. A
// player that is starting or failed reads as stopped. A daemon whose state
// has no soundboard is older than this plugin.
export function boardWord(board) {
  if (!board) return "update";
  if (board.error == null) return null;
  if (board.available === false) return "unavailable";
  if (!board.channel) return "no SFX";
  if (!board.enabled) return "off";
  return "stopped";
}

// A clip's word from its status: null when ready, the board's when the
// board cannot play.
const CLIP_WORDS = { ready: null, loading: "loading", error: "error" };
function clipWord(board, clip) {
  if (clip.status === "off") return boardWord(board) ?? "stopped";
  return Object.hasOwn(CLIP_WORDS, clip.status) ? CLIP_WORDS[clip.status] : "update";
}

// What a clip key shows and does, from the state's mixer and the key's
// settings: {stop, id, clip, label, word, playing}. word is null while a
// press can play; otherwise it is the short reason the key reads in grey.
export function clipKey(mixer, settings) {
  const board = mixer?.soundboard ?? null;
  const target = settings?.target ?? "";
  if (target === STOP_ALL) {
    const word = boardWord(board);
    return { stop: true, id: null, clip: null, label: "Stop all", word,
             playing: word === null && (board.playing?.length ?? 0) > 0 };
  }
  const id = clipIdOf(target);
  if (!id) return { stop: false, id: null, clip: null, label: "Clip", word: "set up", playing: false };
  const clip = board?.clips?.find((c) => c.id === id) ?? null;
  // A removed clip keeps the name the inspector saved with the key.
  const name = clip?.name ?? (typeof settings.name === "string" && settings.name ? settings.name : id);
  const word = !board ? "update" : !clip ? "gone" : clipWord(board, clip);
  return { stop: false, id, clip, label: keyLine(name), word, playing: word === null && clip.playing === true };
}

// Send the effective mode so the daemon and the key agree on whether this
// press needs a release, even if the library mode changes before it arrives.
// Inside a multi-action OpenDeck sends the key up after 100 ms, so hold
// plays as overlap there.
export function clipPress(key, settings, inMultiAction = false) {
  if (key.stop) return { payload: { cmd: STOP_ALL }, hold: false };
  const override = settings?.mode;
  const libraryMode = key.clip?.mode;
  let mode = MODES.includes(libraryMode) ? libraryMode : "overlap";
  if (MODES.includes(override)) mode = override;
  if (mode === "hold" && inMultiAction) mode = "overlap";
  return { payload: { cmd: "playClip", clip: key.id, mode }, hold: mode === "hold" };
}

// Where the clip is, from the meters frame: {position, voices}, or null
// while it is silent and its key is absent.
export function clipProgress(levels, id) {
  const value = id ? levels?.[`clip:${id}`] : null;
  if (!Array.isArray(value) || !Number.isFinite(value[0])) return null;
  return { position: Math.max(0, Math.min(1, value[0])), voices: Number.isFinite(value[1]) ? value[1] : 1 };
}

// The progress bar moves in steps of four pixels on a key; a meters frame
// that does not move it draws nothing. -1 is no bar.
export const progressBucket = (progress) => progress ? Math.round(progress.position * 25) : -1;

// The clip's own icon and colour, each falling back to SFX's from the
// layout, as a channel key shows its channel's. "none" draws no clip icon.
export function clipAppearance(mixer, clip) {
  const sfx = targetAppearance(mixer, `chvol:${mixer?.soundboard?.channel ?? "sfx"}`);
  if (!clip) return sfx;
  const own = appearanceOf({ icon: clip.icon, colour: clip.colour });
  const icon = clip.icon === "none" ? { icon: "", path: null }
    : own.icon || own.path ? { icon: own.icon, path: own.path } : { icon: sfx.icon, path: sfx.path };
  return { ...icon, colour: own.colour ?? sfx.colour };
}

// A sentence for the inspector about why the board cannot play, or null:
// the daemon's own, once it is reachable and has a soundboard.
export function boardNotice(mixer, daemonUp) {
  if (!daemonUp) return "The OpenXLR daemon is not running.";
  const board = mixer?.soundboard ?? null;
  if (!board) return "This OpenXLR daemon has no soundboard. Update OpenXLR.";
  return typeof board.error === "string" && board.error ? board.error : null;
}

// What the inspector lists: every clip by id with its name, own mode and
// error, the board's notice, and whether the list is the daemon's (known),
// so a clip absent from it is missing rather than not yet heard of.
export function clipChoices(mixer, daemonUp) {
  return {
    known: daemonUp && !!mixer?.soundboard,
    clips: (mixer?.soundboard?.clips ?? []).map((c) => ({ id: c.id, name: c.name, mode: c.mode, error: c.error ?? null })),
    notice: boardNotice(mixer, daemonUp),
  };
}
