// Shared with the Node tests. This file has no shell or filesystem access.
function clamp(value, ceiling) {
    return typeof value === "number" && isFinite(value) ? Math.max(0, Math.min(ceiling, value)) : 0;
}

function tokenPath(runtime, config, home) {
    return (runtime || config || home + "/.config") + "/openxlr/token";
}

function mono(id) { return id === "xlr1" || id === "xlr2"; }
function muteControl(id) { return id === "xlr2" ? "mute2" : "mute"; }
function sendMuted(channel, mix) { return (channel.mutedIn || []).indexOf(mix) >= 0; }
function level(channel, mix) { return clamp((channel.levels || {})[mix], 1); }
function ceiling(mix) { return mix.kind === "monitor" ? 1.5 : 1; }
function rms(level) { return level <= 0 ? "<-60" : String(Math.round(level * 60 - 60)); }

function barName(name) { return String(name || "").replace(/^Monitor /, "Mon "); }

var inputHoldMs = 10000;
function shownInputs(lastActive, now) {
    return Object.keys(lastActive).filter(function (id) {
        var time = lastActive[id];
        return mono(id) && typeof time === "number" && isFinite(time) && time <= now && now - time < inputHoldMs;
    });
}

function feedLevel(levels, ids) {
    return ids.reduce(function (level, id) {
        var stereo = pair(levels, "mix:" + id, false);
        return Math.max(level, stereo[0], stereo[1]);
    }, 0);
}

function themePalette(palettes, name) {
    var id = String(name || "").trim();
    return Object.prototype.hasOwnProperty.call(palettes, id) ? palettes[id] : null;
}

function outputName(snapshot, output) {
    var devices = snapshot && Array.isArray(snapshot.devices) ? snapshot.devices : [];
    var device = devices.filter(function (entry) { return entry && entry.name === output; })[0];
    return device && typeof device.description === "string" && device.description.trim()
        ? device.description.trim() : output;
}

function pair(levels, key, isMono) {
    var raw = levels[key];
    if (!Array.isArray(raw) || raw.length === 0) return [0, 0];
    var left = clamp(raw[0], 1);
    var right = raw.length > 1 ? clamp(raw[1], 1) : left;
    return isMono ? [Math.max(left, right), Math.max(left, right)] : [left, right];
}

// The TUI's half-cell horizontal meter and eighth-cell vertical meter.
function horizontalGlyph(level, cell, count) {
    var within = clamp(level, 1) * count - cell;
    return within >= 1 ? "█" : within >= 0.5 ? "▌" : "─";
}
function verticalGlyph(level, row, count) {
    var within = clamp(clamp(level, 1) * count - row, 1);
    return within === 0 ? "│" : " ▁▂▃▄▅▆▇█"[Math.ceil(within * 8)];
}

// One label per selected output, including sums and deliberately silent feeds.
// The daemon marks a channel absent when the active device has no jack
// behind it, which is XLR 2 and Aux In on everything but the Wave XLR Pro.
// An older daemon does not send the field, so a missing one means present.
function shownChannels(channels) {
    return (channels || []).filter(function (channel) { return channel.present !== false; });
}

function monitorFeeds(mixer) {
    var mixes = mixer.mixes || [];
    var first = mixes.filter(function (mix) { return mix.kind === "monitor"; })[0];
    var outputs = mixer.monitorOutputs || (mixer.monitorOutput ? [mixer.monitorOutput] : []);
    return outputs.map(function (output) {
        var feeds = mixer.monitorFeeds || {};
        var feed = Object.prototype.hasOwnProperty.call(feeds, output) ? feeds[output] : (first ? first.id : "");
        var names = String(feed || "").split("+").filter(Boolean).map(function (id) {
            var mix = mixes.filter(function (entry) { return entry.id === id; })[0];
            return mix ? mix.name : id;
        });
        return { output: output, ids: String(feed || "").split("+").filter(Boolean), name: names.join(" + ") || "Silent" };
    });
}

function volumeCommand(entry, mix, master, value) {
    var result = { cmd: master ? "setMixVolume" : "setLevel", mix: master ? entry.id : mix,
        value: clamp(value, master ? ceiling(entry) : 1) };
    if (!master) result.channel = entry.id;
    return result;
}
function muteCommand(entry, mix, master) {
    var result = { cmd: master ? "setMixMuted" : "setChannelMuted", mix: master ? entry.id : mix,
        value: master ? !entry.muted : !sendMuted(entry, mix) };
    if (!master) result.channel = entry.id;
    return result;
}

// Each widget owns one session. Timers and the socket are supplied by QML;
// tests drive these same transitions without contacting the user's daemon.
function Session(io) {
    this.io = io;
    this.ready = false;
    this.snapshot = null;
    this.levels = {};
    this.error = "";
    this.pending = "";
    this.serial = 0;
    this.backoff = 1000;
    this.phase = "idle";
}
Session.prototype.start = function () {
    if (this.phase !== "idle" && this.phase !== "waiting") return;
    this.phase = "token";
    this.io.deadline(5000);
    this.io.readToken();
};
Session.prototype.token = function (text) {
    if (this.phase !== "token") return;
    var token = String(text || "").trim();
    if (!token) { this.lost(); return; }
    this.phase = "connecting";
    this.io.connect(token);
};
Session.prototype.opened = function (token) {
    if (this.phase !== "connecting") return;
    this.phase = "auth";
    this.io.send({ cmd: "auth", token: token });
    this.io.send({ cmd: "getState" });
};
Session.prototype.lost = function () {
    if (this.phase === "waiting" || this.phase === "stopped") return;
    this.phase = "waiting";
    this.ready = false;
    this.snapshot = null;
    this.levels = {};
    this.pending = "";
    this.io.cancelTimers();
    this.io.close();
    this.io.changed();
    this.io.retry(this.backoff);
    this.backoff = Math.min(this.backoff * 2, 10000);
};
Session.prototype.receive = function (raw) {
    if (this.phase !== "auth" && this.phase !== "ready") return;
    var message;
    try { message = JSON.parse(raw); } catch (_) { return; }
    if (!message || typeof message !== "object") return;
    if (message.type === "state" && message.mixer && Array.isArray(message.mixer.channels) && Array.isArray(message.mixer.mixes)) {
        this.snapshot = message;
        this.ready = true;
        this.phase = "ready";
        this.backoff = 1000;
        this.io.deadline(0);
    } else if (message.type === "meters" && this.ready && message.levels && typeof message.levels === "object") {
        this.levels = message.levels;
        this.io.meterDeadline(1000);
    } else if (message.type === "commandResult" && message.requestId === this.pending) {
        this.pending = "";
        this.error = message.error || "";
        this.io.commandDeadline(0);
    } else if (message.type === "error") {
        this.error = message.message || "Command refused";
    }
    this.io.changed();
};
Session.prototype.metersExpired = function () {
    this.levels = {};
    this.io.changed();
};
Session.prototype.commandExpired = function () {
    this.error = "No command reply. Reconnecting; the change may have reached the daemon.";
    this.lost();
};
Session.prototype.send = function (command) {
    if (!this.ready || this.pending) return false;
    this.pending = "omarchy-" + (++this.serial);
    this.error = "";
    command.requestId = this.pending;
    this.io.commandDeadline(5000);
    this.io.send(command);
    this.io.changed();
    return true;
};
Session.prototype.stop = function () {
    this.phase = "stopped";
    this.io.cancelTimers();
    this.io.close();
};
