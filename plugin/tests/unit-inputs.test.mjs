import assert from "node:assert/strict";
import test from "node:test";
import { channelUnitControl, channelUnitTarget, deviceTargetSupported, duckingEnabled, duckingToggle, unitCarrier, unitDialLabel, unitInputName, unitInputOf, unitKeyLabel, unitMeterKey }
  from "../com.emaspa.openxlr.sdPlugin/unit-inputs.mjs";

const unit = "0fd9:00b4@0123456789abcdef";
const pro = {gain:true, mute:true, phantom:true, lowCut:true, expander:true, voiceTune:true, clipGuard:true,
  compressor:true, hpVolume:true, crossfade:true, outputRouting:true, auxInput:true, lowImpedance:true,
  xlrInputs:2, hpOutputs:2, xlrJacks:true};

test("channel controls resolve the current primary or additional input with its own gain range", () => {
  const state = bound();
  state.state = {gainDb:52, mute:false, phantom2:true, lowCut2:false, clipGuard2:true};
  assert.deepEqual(channelUnitControl(state, "cap1", "gain"), {device:unit, control:"gain", value:52, max:80});
  for (const [control, value] of [["phantom", true], ["lowCut", false], ["clipGuard", true]])
    assert.deepEqual(channelUnitControl(state, "xlr2", control), {device:unit, control:`${control}2`, value, max:null});
  const wave3 = "0fd9:0070@fedcba9876543210";
  state.waveInterfaces = [{id:wave3, enabled:true, connected:true,
    capabilities:{gain:true, mute:true, clipGuard:true, gainMaxDb:40}, state:{gainDb:30, mute:true, clipGuard:false}}];
  state.mixer.channels[3].unitInput = {unit:wave3, input:1, primary:false};
  state.mixer.channels[3].binding.unit = wave3;
  state.mixer.channels[3].binding.source = "saved.node.before.rename";
  assert.deepEqual(channelUnitControl(state, "cap1", "gain"), {device:wave3, control:"gain", value:30, max:40});
  assert.deepEqual(channelUnitControl(state, "cap1", "mute"), {device:wave3, control:"mute", value:true, max:null});
  assert.equal(channelUnitControl(state, "cap1", "phantom"), null);
  state.waveInterfaces[0].connected = false;
  assert.equal(channelUnitControl(state, "cap1", "gain"), null);
  state.waveInterfaces[0].connected = true;
  state.waveInterfaces[0].enabled = false;
  assert.equal(channelUnitControl(state, "cap1", "mute"), null);
  assert.equal(channelUnitControl(state, "missing", "gain"), null);
  assert.equal(channelUnitControl(state, "xlr2", "hpVolumeDb"), null);
});

test("channel targets keep their ids and labels through renames and become unavailable on older state", () => {
  const state = bound();
  for (const [prefix, control] of [["chgain", "gain"], ["chmute", "mute"], ["chphantom", "phantom"],
    ["chlowcut", "lowCut"], ["chclipguard", "clipGuard"]])
    assert.deepEqual(channelUnitTarget(`${prefix}:cap1`), {channel:"cap1", control});
  for (const target of [undefined, "gain", "chgain:", "unknown:cap1"])
    assert.equal(channelUnitTarget(target), null);
  state.mixer.channels[3].name = "Guest";
  assert.equal(unitKeyLabel(state, "chmute:cap1"), "Guest\nMute");
  assert.equal(unitDialLabel(state, "chgain:cap1"), "Guest gain");
  assert.equal(unitMeterKey(state.mixer, "chgain:cap1"), "ch:cap1");
  for (const channel of state.mixer.channels) delete channel.unitInput;
  assert.equal(channelUnitControl(state, "cap1", "gain"), null);
  assert.equal(channelUnitControl(state, "xlr2", "mute"), null);
  assert.equal(unitMeterKey(state.mixer, "chgain:cap1"), null);
  assert.equal(unitCarrier(state.mixer, 2).id, "xlr2");
});

// XLR 1 takes a Yeti Nano; the Pro's own first input plays on a capture channel.
function bound() {
  return {connected:true, capabilities:pro, mixer:{channels:[
    {id:"xlr1", name:"Mic 1", hardware:true,
      binding:{kind:"source", source:"alsa_input.usb-Blue_Yeti_Nano", pair:0, monoChannel:0}},
    {id:"xlr2", name:"XLR 2", hardware:true, binding:{kind:"unit", pair:1},
      unitInput:{unit, input:2, primary:true}},
    {id:"music", name:"Music", binding:{kind:"apps"}},
    {id:"cap1", name:"Wave XLR Pro input 1", captureSource:"alsa_input.usb-Elgato_Wave_XLR_Pro",
      binding:{kind:"source", source:"alsa_input.usb-Elgato_Wave_XLR_Pro", pair:0, monoChannel:0},
      unitInput:{unit, input:1, primary:true}},
  ]}};
}

test("the carrier is the channel the daemon names, and only it", () => {
  const state = bound();
  assert.equal(unitCarrier(state.mixer, 1).id, "cap1");
  assert.equal(unitCarrier(state.mixer, 2).id, "xlr2");
  // The daemon names input 2 but not input 1: nothing carries input 1, not
  // even the strip that used to.
  delete state.mixer.channels[3].unitInput;
  assert.equal(unitCarrier(state.mixer, 1), null);
  // An additional unit's input is not the header unit's.
  state.mixer.channels[3].unitInput = {unit:"0fd9:0070@fedcba9876543210", input:1, primary:false};
  assert.equal(unitCarrier(state.mixer, 1), null);
});

test("without unitInput a strip on the interface still carries its input", () => {
  // A daemon that predates bindings, or no unit connected: the strips stand in.
  const old = {mixer:{channels:[{id:"xlr1", name:"XLR 1", hardware:true}, {id:"xlr2", name:"XLR 2", hardware:true}]}};
  assert.equal(unitCarrier(old.mixer, 1).id, "xlr1");
  assert.equal(unitCarrier(old.mixer, 2).id, "xlr2");
  // A bound strip never stands in for the interface.
  old.mixer.channels[0].binding = {kind:"source", source:"alsa_input.webcam", pair:0, monoChannel:0};
  assert.equal(unitCarrier(old.mixer, 1), null);
  assert.equal(unitCarrier(null, 1), null);
  assert.equal(unitCarrier({channels:"nonsense"}, 1), null);
});

test("key faces and dial titles name the carrying channel", () => {
  const state = bound();
  assert.equal(unitKeyLabel(state, "mute"), "Wave XLR P…\nMute");
  assert.equal(unitKeyLabel(state, "phantom2"), "XLR 2\n48V");
  assert.equal(unitDialLabel(state, "gain"), "Wave XLR Pro input 1 gain");
  assert.equal(unitDialLabel(state, "gain2"), "XLR 2 gain");
  state.mixer.channels[3].name = "Pro mic";
  assert.equal(unitKeyLabel(state, "clipGuard"), "Pro mic\nClipGuard");

  // Not in the mixer: the input by number, never a strip it does not feed.
  delete state.mixer.channels[3].unitInput;
  assert.equal(unitInputName(state, 1), "Input 1");
  assert.equal(unitKeyLabel(state, "mute"), "Input 1\nMute");
  assert.equal(unitDialLabel(state, "gain"), "Input 1 gain");

  // Back on the jack the strip's own name returns; a Wave:3's capsule is Mic 1.
  const jack = {mixer:{channels:[{id:"xlr1", name:"XLR 1", hardware:true, binding:{kind:"unit", pair:0},
    unitInput:{unit, input:1, primary:true}}]}};
  assert.equal(unitKeyLabel(jack, "mute"), "XLR 1\nMute");
  jack.mixer.channels[0].name = "Mic 1";
  assert.equal(unitDialLabel(jack, "gain"), "Mic 1 gain");

  // No mixer to look in: the name the interface gives its strips.
  assert.equal(unitInputName({capabilities:pro}, 1), "XLR 1");
  assert.equal(unitInputName({capabilities:{...pro, xlrJacks:false}}, 1), "Mic 1");
  assert.equal(unitInputName(null, 2), "XLR 2");

  // Anything else is not a unit input control.
  for (const target of ["hp", "crossfade", "gainLocked", "softClipGuard", "sendmute:xlr1:monitor", undefined]) {
    assert.equal(unitKeyLabel(state, target), null);
    assert.equal(unitDialLabel(state, target), null);
    assert.equal(unitInputOf(target), null);
  }
});

test("a gain dial meters the channel its input plays on", () => {
  const state = bound();
  assert.equal(unitMeterKey(state.mixer, "gain"), "ch:cap1");
  assert.equal(unitMeterKey(state.mixer, "gain2"), "ch:xlr2");
  delete state.mixer.channels[3].unitInput;
  assert.equal(unitMeterKey(state.mixer, "gain"), null);
  assert.equal(unitMeterKey(state.mixer, "hp"), null);
});

test("hardware targets follow the unit's capabilities whatever XLR 1 takes", () => {
  const state = bound();
  for (const target of ["gain", "mute", "phantom", "lowCut", "expander", "voiceTune", "clipGuard", "compressor",
                        "gain2", "mute2", "phantom2", "hp", "hp2", "crossfade", "lowImpedance", "outHp1",
                        "outLineOut", "auxLevel", "auxLevelLock", "gainLocked"])
    assert.equal(deviceTargetSupported(state.capabilities, target), true, target);
  // One input and one headphone output: the second ones are not there.
  const wave = {gain:true, mute:true, hpVolume:true, xlrInputs:1, hpOutputs:1, xlrJacks:false};
  for (const target of ["gain2", "mute2", "phantom2", "lowCut2", "compressor2", "hp2"])
    assert.equal(deviceTargetSupported(wave, target), false, target);
  assert.equal(deviceTargetSupported(wave, "gain"), true);
  assert.equal(deviceTargetSupported(wave, "phantom"), false);
  // The Line In gain is the Pro's own input, not the Aux In stage.
  assert.equal(deviceTargetSupported({...state.capabilities, lineInput:true}, "lineIn"), true);
  assert.equal(deviceTargetSupported({...state.capabilities, lineInput:false}, "lineIn"), false);
  // So is its level lock, apart from Aux In's.
  assert.equal(deviceTargetSupported({...state.capabilities, lineInput:true}, "lineInLevelLock"), true);
  assert.equal(deviceTargetSupported({...state.capabilities, lineInput:false}, "lineInLevelLock"), false);
  assert.equal(deviceTargetSupported(wave, "lineIn"), false);
  // No unit connected: the daemon sends no capabilities and nothing is there.
  for (const capabilities of [undefined, null, "nonsense"])
    for (const target of ["gain", "mute", "gainLocked", "hp"])
      assert.equal(deviceTargetSupported(capabilities, target), false);
  assert.equal(deviceTargetSupported(pro, "sendmute:xlr1:monitor"), false);
  // Ducking is no hardware target: no capability makes it one.
  assert.equal(deviceTargetSupported({...pro, ducking:true}, "ducking"), false);
});

test("the ducking key follows the state's ducking on any device", () => {
  const ducking = {enabled:true, key:"xlr1", channels:["music"], mixes:["monitor"], backend:"software", device:null};
  // No unit at all, a unit without hardware ducking and a Pro all read it.
  assert.equal(duckingEnabled({connected:false, ducking}), true);
  assert.equal(duckingEnabled({connected:true, capabilities:{gain:true}, ducking:{...ducking, enabled:false}}), false);
  assert.equal(duckingEnabled({connected:true, capabilities:{...pro, ducking:true}, ducking}), true);
  // A daemon that sends no ducking leaves the key unavailable.
  for (const state of [undefined, null, {connected:true, capabilities:{...pro, ducking:true}}, {ducking:null}])
    assert.equal(duckingEnabled(state), null);
});

test("a ducking key is offered without a Pro and a press flips the switch alone", () => {
  const ducking = {enabled:false, key:"xlr1", channels:["music", "game"], mixes:["monitor", "stream"],
    sensitivity:"medium", amount:"high", speed:"fast", processing:"device", backend:null, available:true,
    error:null, device:null};
  // A dock without hardware ducking, and no interface at all, still offer the key.
  const dock = {connected:true, capabilities:{gain:true, mute:true, xlrInputs:1}, ducking};
  const none = {connected:false, ducking:{...ducking, enabled:true, backend:"software"}};
  assert.equal(duckingEnabled(dock), false);
  assert.equal(duckingEnabled(none), true);
  // The press sends the switch only, so the key, channels, mixes and presets stay as they are.
  assert.deepEqual(duckingToggle(dock), {cmd:"setDucking", value:{enabled:true}});
  assert.deepEqual(duckingToggle(none), {cmd:"setDucking", value:{enabled:false}});
  // On a Pro it is the same command, whichever side runs it.
  const onPro = {connected:true, capabilities:{...pro, ducking:true},
    ducking:{...ducking, enabled:true, backend:"device", device:{unit, name:"Wave XLR Pro", ready:true, held:true, outputs:["monitor"]}}};
  assert.deepEqual(duckingToggle(onPro), {cmd:"setDucking", value:{enabled:false}});
  // Hardware-control mode: the daemon still sends ducking and refuses the command itself.
  assert.deepEqual(duckingToggle({connected:true, ducking}), {cmd:"setDucking", value:{enabled:true}});
  // No ducking from the daemon: nothing to send.
  for (const state of [undefined, null, {connected:true}, {ducking:null}, {ducking:"on"}])
    assert.equal(duckingToggle(state), null);
});

test("raw capture keys need a unit with raw capture, and the input", () => {
  const raw = {...pro, rawCapture:true, lineInput:true};
  for (const target of ["rawCapture", "rawCapture2", "lineInRawCapture"]) {
    assert.equal(deviceTargetSupported(raw, target), true, target);
    assert.equal(deviceTargetSupported({...raw, rawCapture:false}, target), false, target);
    assert.equal(deviceTargetSupported(pro, target), false, target);
  }
  // A one-input unit with raw capture and no Line In: XLR 1 only.
  const mk2 = {gain:true, mute:true, xlrInputs:1, hpOutputs:1, rawCapture:true};
  assert.equal(deviceTargetSupported(mk2, "rawCapture"), true);
  assert.equal(deviceTargetSupported(mk2, "rawCapture2"), false);
  assert.equal(deviceTargetSupported(mk2, "lineInRawCapture"), false);
  assert.equal(unitInputOf("rawCapture"), 1);
  assert.equal(unitInputOf("rawCapture2"), 2);
  assert.equal(unitInputOf("lineInRawCapture"), null);
});
