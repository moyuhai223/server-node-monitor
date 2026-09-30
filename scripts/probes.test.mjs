import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

// Import as module independently of the caller's package.json/type.
const asModule = async path => import('data:text/javascript;base64,' + Buffer.from(await readFile(new URL(path, import.meta.url), 'utf8')).toString('base64'));
const { probeStats } = await asModule('../web/themes/panorama/probes.js');

test('probe statistics distinguish no data, unsupported, loss, real zero RTT and stale data', () => {
  assert.equal(probeStats({ points: [], interval: 10 }).failurePct, null);
  assert.equal(probeStats({ points: [{ state: 3, us: -1, ts: 1000 }], interval: 10 }, 1000).failurePct, null);
  const stats = probeStats({ interval: 10, points: [
    { state: 0, us: 0, ts: 1000 }, { state: 0, us: 20000, ts: 2000 },
    { state: 1, us: -1, ts: 3000 }, { state: 2, us: -1, ts: 4000 }, { state: 3, us: -1, ts: 5000 },
  ] }, 100000);
  assert.equal(stats.failurePct, 50); assert.equal(stats.averageMs, 10); assert.equal(stats.samples, 4); assert.equal(stats.stale, true);
});

test('SDK accepts streamed probe updates, removes deleted targets and handles older snapshots', async () => {
  const handlers = new Map();
  const connection = { on: (name, fn) => handlers.set(name, fn), onreconnecting() {}, onreconnected() {}, onclose() {}, async start() {}, async stop() {} };
  globalThis.window = {};
  globalThis.signalR = {
    HttpTransportType: { WebSockets: 1, LongPolling: 4 }, LogLevel: { Warning: 3 },
    protocols: { msgpack: { MessagePackHubProtocol: class {} } },
    HubConnectionBuilder: class {
      withUrl() { return this; } withHubProtocol() { return this; } withAutomaticReconnect() { return this; }
      configureLogging() { return this; } build() { return connection; }
    },
  };
  const { createClient } = await asModule('../web/sdk/snm-client.js');
  const client = createClient({ tick: false }); await client.start();
  handlers.get('snapshot')({ nodes: [{ id: 1, name: 'old agent', live: { status: 1 } }] });
  assert.deepEqual(client.state.nodes[0].probes, []);
  let changes = 0; client.on('probes', () => changes++);
  handlers.get('probes')({ id: 1, probes: [{ id: 2, kind: 1, interval: 30, points: [{ ts: 1000n, state: 0, us: 12500n }] }] });
  assert.equal(client.state.nodes[0].probes[0].points[0].us, 12500); assert.equal(changes, 1);
  handlers.get('nodes')([{ id: 1, probes: [] }]);
  assert.deepEqual(client.state.nodes[0].probes, []);
  handlers.get('nodes')([]); handlers.get('probes')({ id: 1, probes: [] });
  assert.equal(client.state.nodes.length, 0); assert.equal(changes, 1);
  await client.stop(); delete globalThis.window; delete globalThis.signalR;
});
