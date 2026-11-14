// The glitch marker key: a Toggle key whose press puts a marker on the
// daemon's event timeline. The next diagnostics archive shows the marker
// with what changed in the 30 seconds before it (timeline.txt). The key is
// momentary: it never lights, is available while the daemon is connected,
// and the host's check mark or alert says whether the daemon took it.

export const GLITCH_TARGET = "markGlitch";

export const isGlitchTarget = (target) => target === GLITCH_TARGET;

// The command a press sends; the label says where the marker came from.
export const glitchPress = () => ({cmd: "markGlitch", label: "Deck"});

// The key's built-in label, drawn inside the key image.
export const GLITCH_LABEL = "Mark\nGlitch";

// Dark but usable while the daemon has sent a state; grey (null) otherwise.
export const glitchValue = (connected) => connected ? false : null;
