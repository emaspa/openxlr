// Shared with the Node tests. This file has no shell or filesystem access.
function clamp(value, ceiling) {
    return typeof value === "number" && isFinite(value) ? Math.max(0, Math.min(ceiling, value)) : 0;
}

function tokenPath(runtime, config, home) {
    return (runtime || config || home + "/.config") + "/openxlr/token";
}

function mono(channel) {
    return typeof channel.microphone === "boolean" ? channel.microphone : channel.id === "xlr1" || channel.id === "xlr2";
}
function sendMuted(channel, mix) { return (channel.mutedIn || []).indexOf(mix) >= 0; }
function level(channel, mix) { return clamp((channel.levels || {})[mix], 1); }
function masterMuted(mix, snapshot) {
    return !!mix.muted || mix.kind === "monitor" && !!(snapshot && snapshot.mixer && snapshot.mixer.monitorMuted);
}
function ceiling(mix) { return mix.kind === "monitor" ? 1.5 : 1; }
function rms(level) { return level <= 0 ? "<-60" : String(Math.round(level * 60 - 60)); }

function barName(name) { return String(name || "").replace(/^Monitor /, "Mon "); }

var inputHoldMs = 10000;
function shownInputs(channels, lastActive, now) {
    return shownChannels(channels).filter(function (channel) {
        var time = lastActive[channel.id];
        return mono(channel) && typeof time === "number" && isFinite(time) && time <= now && now - time < inputHoldMs;
    }).slice(0, 4).map(function (channel) { return channel.id; });
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

// The interface's gain, mute and phantom power belong to its input stage,
// not to a strip. The daemon names the channel that carries each of the
// primary unit's two inputs in that channel's unitInput: XLR 1 and XLR 2
// while they take the interface, or a capture channel on the interface's own
// node while a strip is bound to another microphone. While no channel names
// one, as with no unit connected or an older daemon, a strip still on the
// interface stands for its input, as it always has.
function carriesPrimaryUnitInput(channel) {
    return !!channel && !!channel.unitInput && channel.unitInput.primary === true;
}
function unitCarrier(mixer, input) {
    var channels = mixer && Array.isArray(mixer.channels) ? mixer.channels : [];
    var named = channels.filter(function (channel) {
        return carriesPrimaryUnitInput(channel) && channel.unitInput.input === input;
    })[0];
    if (named)
        return named;
    // The daemon names the other input but not this one: no channel carries it.
    if (channels.some(carriesPrimaryUnitInput))
        return null;
    var id = input === 2 ? "xlr2" : "xlr1";
    var strip = channels.filter(function (channel) {
        return !!channel && channel.hardware === true && channel.id === id;
    })[0];
    return strip && (!strip.binding || strip.binding.kind === "unit") ? strip : null;
}
// The unit control behind a channel's microphone mute: the primary's mute or
// mute2 on the channel carrying its input 1 or 2 (device ""), or a driven
// additional unit's on the channel its unitInput names (device: that unit's
// id, written with setWaveControl). null for every other channel.
function additionalUnit(snapshot, id) {
    var units = snapshot && Array.isArray(snapshot.waveInterfaces) ? snapshot.waveInterfaces : [];
    return units.filter(function (unit) { return !!unit && unit.active !== true && unit.id === id; })[0] || null;
}
function muteTarget(snapshot, id) {
    var mixer = snapshot && snapshot.mixer ? snapshot.mixer : null;
    var first = unitCarrier(mixer, 1);
    if (first && first.id === id)
        return { device: "", control: "mute" };
    var second = unitCarrier(mixer, 2);
    if (second && second.id === id)
        return { device: "", control: "mute2" };
    var channels = mixer && Array.isArray(mixer.channels) ? mixer.channels : [];
    var channel = channels.filter(function (entry) { return !!entry && entry.id === id; })[0];
    var input = channel && channel.unitInput && channel.unitInput.primary === false ? channel.unitInput : null;
    var unit = input ? additionalUnit(snapshot, input.unit) : null;
    return unit && unit.enabled === true ? { device: unit.id, control: input.input === 2 ? "mute2" : "mute" } : null;
}
function muteControl(snapshot, id) {
    var target = muteTarget(snapshot, id);
    return target ? target.control : "";
}
// The state and capabilities the target's unit reports, or {} for either.
function targetState(snapshot, target) {
    if (!target)
        return {};
    var holder = target.device === "" ? snapshot : additionalUnit(snapshot, target.device);
    return (holder && holder.state) || {};
}
function hardwareMuted(snapshot, id) {
    var target = muteTarget(snapshot, id);
    return !!target && !!targetState(snapshot, target)[target.control];
}
// The mute can be written now: its unit is connected and has a mute.
function muteAvailable(snapshot, id) {
    var target = muteTarget(snapshot, id);
    if (!target)
        return false;
    var holder = target.device === "" ? snapshot : additionalUnit(snapshot, target.device);
    return !!holder && holder.connected === true && !!(holder.capabilities || {}).mute;
}
function muteCommandFor(snapshot, id, value) {
    var target = muteTarget(snapshot, id);
    if (!target)
        return null;
    return target.device === "" ? { cmd: "set", control: target.control, value: value }
        : { cmd: "setWaveControl", device: target.device, control: target.control, value: value };
}

// A microphone strip bound to one port of another capture source.
function sourceBound(channel) {
    return !!channel && channel.hardware === true && !!channel.binding && channel.binding.kind === "source"
        && typeof channel.binding.source === "string" && channel.binding.source !== "";
}
// A supported unit can rename its capture node when its card profile changes.
// Follow the same unique source as the daemon; an ambiguous unit stays named
// by the saved node rather than borrowing another source's description.
function bindingSource(snapshot, channel) {
    var source = channel.binding.source;
    var devices = snapshot && Array.isArray(snapshot.devices) ? snapshot.devices : [];
    if (devices.some(function (entry) { return !!entry && entry.kind === 1 && entry.name === source; }))
        return source;
    var unit = channel.binding.unit;
    if (typeof unit !== "string" || !unit)
        return source;
    var matches = devices.filter(function (entry) {
        return !!entry && entry.kind === 1 && !entry.isOwn && entry.drivenUnit === unit;
    });
    return matches.length === 1 ? matches[0].name : source;
}
// What feeds a bound strip: the source's description and its port, or why
// it is silent. "" for every other channel.
function bindingNote(snapshot, channel) {
    if (!sourceBound(channel))
        return "";
    if (typeof channel.bindingWarning === "string" && channel.bindingWarning)
        return channel.bindingWarning;
    var port = typeof channel.binding.monoChannel === "number" && isFinite(channel.binding.monoChannel)
        ? channel.binding.monoChannel + 1 : 1;
    return outputName(snapshot, bindingSource(snapshot, channel)) + ", port " + port
        + (channel.bindingConnected === false ? ", not linked" : "");
}
// The strip's line under its name: what kind of channel it is, or for a
// bound strip its source, short, and OFF while that source is silent.
function kindLine(snapshot, channel) {
    if (sourceBound(channel))
        return outputName(snapshot, bindingSource(snapshot, channel)) + (channel.bindingConnected === false ? " OFF" : "");
    var kind = channel.hardware ? "INPUT" : channel.captureSource ? "CAPTURE" : "APP";
    return kind + (channel.captureSource && !channel.captureConnected ? " OFF" : "");
}
// A channel's name from the state, or "" when the state has no such channel.
function channelName(channels, id) {
    var channel = (channels || []).filter(function (entry) { return !!entry && entry.id === id; })[0];
    return channel && typeof channel.name === "string" ? channel.name : "";
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
    var monitors = mixes.filter(function (mix) { return mix.kind === "monitor"; });
    var first = monitors.filter(function (mix) { return mix.id === mixer.defaultMonitorMix; })[0]
        || monitors[0];
    var outputs = mixer.monitorOutputs || (mixer.monitorOutput ? [mixer.monitorOutput] : []);
    return outputs.map(function (output) {
        var feeds = mixer.monitorFeeds || {};
        var saved = Object.prototype.hasOwnProperty.call(feeds, output) ? feeds[output] : "";
        var parts = String(saved || "").split("+").map(function (id) { return id.trim(); });
        var chosen = mixes.filter(function (mix) { return parts.indexOf(mix.id) >= 0; });
        if (!chosen.length && first) chosen = [first];
        return { output: output, ids: chosen.map(function (mix) { return mix.id; }),
            name: chosen.map(function (mix) { return mix.name; }).join(" + ") || "Silent" };
    });
}

function volumeCommand(entry, mix, master, value) {
    var result = { cmd: master ? "setMixVolume" : "setLevel", mix: master ? entry.id : mix,
        value: clamp(value, master ? ceiling(entry) : 1) };
    if (!master) result.channel = entry.id;
    return result;
}
function muteCommand(entry, mix, master, snapshot) {
    var result = { cmd: master ? "setMixMuted" : "setChannelMuted", mix: master ? entry.id : mix,
        value: master ? !masterMuted(entry, snapshot) : !sendMuted(entry, mix) };
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
