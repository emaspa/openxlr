import { TabAudio, errorText } from './routing.mjs';

const audio = new TabAudio({
  mediaDevices: navigator.mediaDevices,
  createContext: () => new AudioContext(),
  status: (id, capturing, error) => { void chrome.runtime.sendMessage({ target: 'worker', type: 'audioStatus', id, capturing, error }).catch(() => {}); },
});

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (sender.id !== chrome.runtime.id || message?.target !== 'offscreen') return;
  const operation = async () => {
    switch (message.type) {
      case 'start': await audio.start(message.id, message.streamId, message.deviceId, message.volume); break;
      case 'volume': audio.setVolume(message.id, message.volume); break;
      case 'route': await audio.route(message.id, message.deviceId); break;
      case 'stop': await audio.stop(message.id, message.error ?? null); break;
      case 'stopAll': await audio.stopAll(message.error ?? null); break;
      case 'snapshot': return audio.snapshot();
      case 'devices': return (await navigator.mediaDevices.enumerateDevices()).map(({ kind, label, deviceId }) => ({ kind, label, deviceId }));
      default: throw new Error('Unknown audio operation.');
    }
    return null;
  };
  void operation().then(value => reply({ value }), error => reply({ error: errorText(error) }));
  return true;
});

navigator.mediaDevices.addEventListener('devicechange', () => { void audio.checkDevices().catch(error => audio.stopAll(errorText(error))); });
