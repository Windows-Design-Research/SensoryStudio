const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const html = fs.readFileSync(path.join(__dirname, '..', 'Assets', 'ZenGarden', 'zen-garden.html'), 'utf8');
const audioSource = html.match(/const audio = \(\(\) => \{[\s\S]*?\n\}\)\(\);/)?.[0];
const bridgeSource = html.match(/  function apply\(msg\)\{[\s\S]*?\n  \}/)?.[0];
assert.ok(audioSource && bridgeSource, 'Real audio module and host bridge must be found');
for (const script of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g))
  new vm.Script(script[1]);

function harness(initialVolume) {
  const contexts = [];
  class Param {
    value = 0;
    setValueAtTime(value) { this.value = value; }
    setTargetAtTime(value) { this.value = value; }
    linearRampToValueAtTime(value) { this.value = value; }
    exponentialRampToValueAtTime(value) { this.value = value; }
    cancelScheduledValues() {}
  }
  class Node {
    gain = new Param();
    frequency = new Param();
    Q = new Param();
    connections = [];
    immediateStops = 0;
    connect(node) { this.connections.push(node); }
    start(time = 0) { this.started = time; }
    stop(time) {
      if (time === undefined) this.immediateStops++;
      this.stopTime = time;
    }
  }
  class Context {
    state = 'running';
    sampleRate = 100;
    currentTime = 5;
    destination = {};
    sources = [];
    gains = [];
    resumes = 0;
    constructor() { contexts.push(this); }
    createGain() { const node = new Node(); this.gains.push(node); return node; }
    createBiquadFilter() { return new Node(); }
    createBuffer(channels, length) { return { getChannelData: () => new Float32Array(length) }; }
    createBufferSource() { const node = new Node(); this.sources.push(node); return node; }
    createOscillator() { return this.createBufferSource(); }
    resume() { this.resumes++; this.state = 'running'; }
    suspend() { assert.fail('Changing volume must not suspend the audio clock'); }
  }
  const sandbox = {
    window: { __soundVolume: initialVolume, AudioContext: Context },
    plantModeActive: false,
    setPaused: value => { sandbox.paused = value; },
    paused: false,
  };
  vm.createContext(sandbox);
  vm.runInContext(`${audioSource}\n${bridgeSource}\nglobalThis.audio = audio; globalThis.apply = apply;`, sandbox);
  return { sandbox, contexts, audio: sandbox.audio, volume: value => sandbox.apply({ type: 'volume', value }) };
}

const cold = harness(0);
cold.audio.ensure();
cold.audio.pickup();
cold.audio.setDrag(1);
assert.equal(cold.contexts.length, 0, 'Saved mute blocks lazy context creation');
cold.volume(0.37);
assert.equal(cold.contexts.length, 0, 'Enabling sound alone must not start audio');
cold.audio.ensure();
assert.equal(cold.contexts[0].gains[0].gain.value, 0.37, 'Lazy creation uses latest saved level');

const live = harness(0.42);
live.audio.ensure();
const ctx = live.contexts[0];
assert.equal(ctx.gains[0].gain.value, 0.42, 'Bootstrap respects saved volume');
live.audio.setDrag(1);
assert.equal(ctx.gains[1].gain.value, 0.16, 'Existing sand envelope is unchanged');
const playAll = () => {
  live.audio.pickup();
  live.audio.drop(0.5);
  live.audio.clack(0.5);
  live.audio.contact();
  live.audio.snap();
};
playAll();
assert.equal(ctx.sources.length, 11, 'All five cues create their original two sources');
assert.equal(ctx.sources[2].started, 5.03, 'Pickup second-tone timing is preserved');
assert.equal(ctx.sources[6].started, 5.022, 'Clack second-burst timing is preserved');
for (const gain of ctx.gains.slice(2))
  assert.ok(gain.connections.includes(ctx.gains[0]), 'Every transient routes through master gain');

const ended = ctx.sources[1];
ended.onended();
live.volume(0);
assert.equal(ctx.gains[0].gain.value, 0, 'Mute immediately silences master');
assert.equal(ctx.gains[1].gain.value, 0, 'Mute clears ongoing drag gain');
assert.equal(ended.immediateStops, 0, 'Ended cues are removed from cancellation tracking');
assert.ok(ctx.sources.slice(2).every(source => source.immediateStops === 1), 'In-flight cues are cancelled');
assert.equal(ctx.sources[0].immediateStops, 0, 'Loop clock is not stopped');
ctx.state = 'suspended';
playAll();
live.audio.ensure();
live.audio.setDrag(1);
assert.equal(ctx.sources.length, 11, 'Muted future cues are skipped');
assert.equal(ctx.resumes, 0, 'Muted input does not resume a suspended context');
assert.equal(ctx.gains[1].gain.value, 0, 'Muted dragging stays silent');
live.volume(0);
assert.ok(ctx.sources.slice(2).every(source => source.immediateStops === 1), 'Repeated mute is idempotent');

live.sandbox.apply({ type: 'pause', value: true });
live.volume(0.42);
assert.equal(ctx.gains[0].gain.value, 0.42, 'Re-enable restores saved level');
assert.equal(ctx.resumes, 0, 'Re-enable does not resume audio');
assert.equal(ctx.sources.length, 11, 'Re-enable does not replay cancelled cues');
assert.equal(live.sandbox.paused, true, 'Volume messages preserve pause state');
live.audio.ensure();
assert.equal(ctx.resumes, 1, 'Normal input retains existing context-resume behavior');
live.audio.pickup();
assert.equal(ctx.sources.length, 13, 'New cues work after re-enable');
live.sandbox.plantModeActive = true;
playAll();
live.audio.setDrag(1);
assert.equal(ctx.sources.length, 13, 'Plant editor audio gating remains intact');
assert.equal(ctx.gains[1].gain.value, 0, 'Plant editor still suppresses dragging');

live.volume(NaN);
live.volume('0.7');
assert.equal(ctx.gains[0].gain.value, 0.42, 'Malformed host volumes are ignored');
live.volume(2);
assert.equal(ctx.gains[0].gain.value, 1);
live.volume(-1);
assert.equal(ctx.gains[0].gain.value, 0);
for (const initial of [undefined, NaN, 'invalid']) {
  const standalone = harness(initial);
  standalone.audio.ensure();
  assert.equal(standalone.contexts[0].gains[0].gain.value, 0.6, 'Standalone fallback stays at 0.6');
}
console.log('Passed Zen Garden audio and host-volume regressions; all inline JavaScript parses (no browser/audio used).');
