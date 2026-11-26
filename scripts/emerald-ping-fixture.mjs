import { readFile } from 'node:fs/promises';
import path from 'node:path';
import Module, { createRequire } from 'node:module';

// Execute the real Vue composables without a DOM; only the public SDK boundary is a fixture.
export async function renderPingFixture(source, input, dependencySource = source) {
  const require = createRequire(path.resolve(dependencySource, 'package.json'));
  const ts = require('typescript');
  const vue = require('vue');
  const mapping = await import('data:text/javascript;base64,' + Buffer.from(await readFile(path.join(dependencySource, 'src/utils/snm-mapping.mjs'), 'utf8')).toString('base64'));
  const listeners = new Set();
  const api = {
    getNodeMetrics: async () => mapping.toMetricsResponse(input.node, input.now),
    subscribeUpdates: fn => { listeners.add(fn); return () => listeners.delete(fn); },
  };
  async function load(relative, mocks) {
    const filename = path.resolve(source, relative);
    const text = await readFile(filename, 'utf8');
    const compiled = ts.transpileModule(text, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
    const instance = new Module(filename);
    instance.filename = filename;
    instance.paths = [];
    instance.require = name => name in mocks ? mocks[name] : require(name);
    instance._compile(compiled, filename);
    return instance.exports;
  }
  const stats = await load('src/composables/useNodePingStats.ts', { '@/utils/api': api });
  const display = await load('src/composables/useNodePingDisplay.ts', {
    '@/composables/useNodePingStats': stats,
    '@/stores/app': { useAppStore: () => ({ pingNetworkOrder: [] }) },
    '@/utils/helper': { formatDateTime: value => value },
  });
  const scope = vue.effectScope();
  try {
    const view = scope.run(() => display.useNodePingDisplay(String(input.node.id)));
    for (let i = 0; i < 4; i++) { await new Promise(resolve => setImmediate(resolve)); await vue.nextTick(); }
    return {
      fixture: input.fixture,
      latency: view.latencyDisplay.value,
      loss: view.lossDisplay.value,
      hasData: view.pingStats.hasData.value,
      availability: view.pingStats.stats.value.availability ?? null,
      networks: view.topPingNetworks.value.map(({name,latency}) => ({name,latency})),
      tooltips: { latency: view.latencyPanelTooltip.value, loss: view.lossPanelTooltip.value },
    };
  } finally { scope.stop(); }
}
