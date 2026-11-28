import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { toMonitorNode, toMetricsResponse, probeSummary } from '../web/emerald-snm/src/utils/snm-mapping.mjs';
const now=1800000000000;
const node={id:1,name:'SG Node',cc:'SG',order:2,cores:4,memMb:2048,diskMb:40960,tLimit:1000000000000,live:{online:true,ts:now,cpu:125,mem:250,disk:500,rx:12345,tx:45678,up:1000,tUsed:200000000},hist:{cpu:[100,125],mem:[200,250],rx:[10,20],tx:[30,40],ts:[0,now]}};
test('maps SNM permille, MiB, bytes/sec and Unix milliseconds exactly once',()=>{const result=toMonitorNode(node,{showSpecs:true});assert.equal(result.country,'SG');assert.equal(result.cpu_cores,4);assert.equal(result.metrics.cpu,12.5);assert.equal(result.metrics.mem_used,536870912);assert.equal(result.metrics.disk_used,21474836480);assert.equal(result.metrics.net_rx,12345);assert.equal(result.last_seen,now/1000);assert.equal(result.month_used,200000000);});
test('private fields are never inferred from labels or IP databases',()=>{const result=toMonitorNode({...node,ipv4:'217.142.185.22',price:99,os:'forged'});assert.equal(result.country,'SG');assert.equal(result.ipv4,undefined);assert.equal(result.price,undefined);assert.equal(result.os,'');});
test('honors public traffic/spec visibility switches',()=>{const result=toMonitorNode(node,{showSpecs:false,showTraffic:false});assert.equal(result.cpu_cores,0);assert.equal(result.traffic_limit,0);assert.equal(result.month_used,undefined);});
test('history has truthful units and reconstructs only unknown SDK timestamps',()=>{const result=toMetricsResponse(node,now);assert.equal(result.metrics.length,2);assert.equal(result.metrics[0].ts,(now-2000)/1000);assert.equal(result.metrics[1].cpu,12.5);assert.equal(result.metrics[1].mem_used,536870912);assert.equal(result.metrics[1].disk_used,undefined);});
test('probe failures are gaps, real zero RTT is valid and unsupported is excluded',()=>{const n={...node,probes:[{id:10,name:'SG TCP',interval:10,points:[{ts:now-3000,state:0,us:0},{ts:now-2000,state:1,us:0},{ts:now-1000,state:2,us:0},{ts:now,state:3,us:0}]}]};const result=toMetricsResponse(n,now);assert.equal(result.ping.length,3);assert.equal(result.ping[0].latency,0);assert.equal(result.ping[1].latency,null);assert.equal(result.ping[2].latency,null);assert.equal(result.loss[10],200/3);});
test('no measurements do not become zero loss or zero latency',()=>{const result=probeSummary({points:[]},now);assert.equal(result.failure,null);assert.equal(result.average,null);assert.equal(result.stale,true);const unsupported=toMetricsResponse({...node,probes:[{id:3,name:'unsupported',points:[{state:3,ts:now,us:0}]}]},now);assert.equal(unsupported.loss[3],undefined);assert.equal(unsupported.ping.length,0);});
test('stale probes are recognized relative to the configured interval',()=>{const probe={interval:10,points:[{ts:now-60000,state:0,us:10000}]};assert.equal(probeSummary(probe,now).stale,true);assert.equal(probeSummary({...probe,interval:60},now).stale,false);});
test('SNM entry is SDK-only and hash routing preserves root and preview refreshes',async()=>{const api=await readFile(new URL('../web/emerald-snm/src/utils/api.ts',import.meta.url),'utf8');const routes=await readFile(new URL('../web/emerald-snm/src/router/index.ts',import.meta.url),'utf8');const manifest=JSON.parse(await readFile(new URL('../web/emerald-snm/theme.json',import.meta.url),'utf8'));assert.match(api,/theme: document\.documentElement\.dataset\.snmTheme \|\| 'emerald'/);assert.doesNotMatch(api,/fetch\(/);assert.match(routes,/createWebHashHistory\(\)/);assert.equal(manifest.id,'emerald');assert.equal(manifest.sdk,1);});

test('hidden specs keep truthful live percentages without exposing hardware sizes', () => {
  const result = toMonitorNode(node, {showSpecs:false});
  assert.equal(result.mem_total, 0); assert.equal(result.disk_total, 0);
  assert.equal(result.metrics.mem_percent, 25); assert.equal(result.metrics.disk_percent, 50);
});
test('latest failure, unsupported and stale probes never reuse an old successful RTT', () => {
  for (const state of [1,2,3]) {
    const probe = {id:1,interval:10,points:[{ts:now-1000,state:0,us:12000},{ts:now,state,us:0}]};
    assert.equal(probeSummary(probe,now).latest, null);
    const result = toMetricsResponse({...node,probes:[probe]},now);
    assert.equal(result.probeMeta[1].latest, null); assert.equal(result.probeMeta[1].lastState,state);
    assert.equal(result.probeMeta[1].interval,10);
  }
  assert.equal(probeSummary({interval:10,points:[{ts:now-60000,state:0,us:12000}]},now).latest,null);
  assert.equal(probeSummary({interval:10,points:[{ts:now,state:0,us:0}]},now).latest,0);
});
test('all-failed probes retain 100% loss and null RTT', () => {
  const result = toMetricsResponse({...node,probes:[{id:4,name:'failed',interval:10,points:[{ts:now,state:1,us:0}]}]},now);
  assert.equal(result.loss[4],100); assert.equal(result.ping[0].latency,null);
});
test('release and Docker build the optional source with a frozen lockfile', async () => {
  const build=await readFile(new URL('./build-web.sh',import.meta.url),'utf8');
  const docker=await readFile(new URL('../deploy/docker/Dockerfile',import.meta.url),'utf8');
  const workflow=await readFile(new URL('../.github/workflows/master-build.yml',import.meta.url),'utf8');
  assert.ok(build.includes('cd web/emerald-snm &&'));
  assert.ok(build.includes('run build)'));
  assert.match(build,/install --frozen-lockfile --ignore-scripts/);
  assert.match(docker,/pnpm install --frozen-lockfile --ignore-scripts/);
  assert.equal(workflow.split('package_json_file: web/emerald-snm/package.json').length - 1,2);
});


test('node card shows the host operating system beside the region flag', async () => {
  const card = await readFile(new URL('../web/emerald-snm/src/components/NodeCard.vue', import.meta.url), 'utf8');
  assert.match(card, /getOSImage\(props\.node\.os\)/);
  assert.match(card, /getOSName\(props\.node\.os\)/);
  assert.match(card, /操作系统：\$\{getOSName\(props\.node\.os\)\}/);
});
test('footer links to SNM source while preserving the Emerald upstream credit', async () => {
  const footer = await readFile(new URL('../web/emerald-snm/src/components/Footer.vue', import.meta.url), 'utf8');
  const [project, theme] = [...footer.matchAll(/<a\b([^>]+)>([\s\S]*?)<\/a>/g)];
  assert.ok(project && theme, 'both footer credits remain present');
  assert.match(project[1], /href="https:\/\/github\.com\/moyuhai223\/server-node-monitor"/);
  assert.match(project[2], />SNM<\/span>/);
  assert.match(theme[1], /href="https:\/\/github\.com\/binaryu\/monitor-theme-emerald"/);
  assert.match(theme[2], />Emerald<\/span>/);
  for (const link of [project, theme]) {
    assert.match(link[1], /target="_blank"/);
    assert.match(link[1], /rel="noopener noreferrer"/);
  }
});


// Exercise the actual Vue card/list composables, not just their source strings.
import { renderPingFixture } from './emerald-ping-fixture.mjs';
import { fileURLToPath } from 'node:url';
const emeraldSource = fileURLToPath(new URL('../web/emerald-snm/', import.meta.url));
const probeNow = 1800000000000;
const point = (state, us, ts = probeNow) => ({state, us, ts});
const task = (points, extra = {}) => ({id:1, name:'ICMP 线路', interval:30, points, ...extra});
const displayProbe = probes => renderPingFixture(emeraldSource, {fixture:'probe-card', now:probeNow, node:{id:11, probes}});

test('probe card retains unsupported targets and explains the agent-side ICMP requirement', async () => {
  const result = await displayProbe([task([point(0,12000,probeNow-30000),point(3,-1)])]);
  assert.equal(result.availability,'unsupported'); assert.equal(result.hasData,false);
  assert.equal(result.latency,'不支持'); assert.equal(result.loss,'不支持');
  assert.deepEqual(result.networks,[{name:'ICMP 线路',latency:'不支持'}]);
  assert.match(result.tooltips.latency,/CAP_NET_RAW/);
});
test('probe card distinguishes unconfigured targets from configured targets awaiting samples', async () => {
  const empty = await displayProbe([]); const waiting = await displayProbe([task([])]);
  assert.equal(empty.latency,'未配置探测'); assert.equal(empty.hasData,false);
  assert.equal(waiting.latency,'等待测量'); assert.equal(waiting.loss,'等待测量');
  assert.deepEqual(waiting.networks,[{name:'ICMP 线路',latency:'等待测量'}]);
});
test('probe card marks stale samples rather than presenting an old RTT', async () => {
  const result = await displayProbe([task([point(0,12000,probeNow-120000)])]);
  assert.equal(result.availability,'stale'); assert.equal(result.hasData,false);
  assert.equal(result.latency,'已过期'); assert.equal(result.loss,'已过期');
  assert.equal(result.networks[0].latency,'已过期');
});
test('probe card preserves real zero RTT and converts microseconds only once', async () => {
  const zero = await displayProbe([task([point(0,0)])]);
  const result = await displayProbe([task([point(0,0,probeNow-30000),point(0,40000)])]);
  assert.equal(zero.latency,'0 ms'); assert.equal(zero.loss,'0.0%');
  assert.equal(result.hasData,true); assert.equal(result.latency,'20 ms');
  assert.equal(result.loss,'0.0%'); assert.equal(result.networks[0].latency,'20ms');
});
test('probe card counts timeouts and errors as 100% loss without inventing zero RTT', async () => {
  const result = await displayProbe([task([point(1,-1,probeNow-30000),point(2,-1)])]);
  assert.equal(result.hasData,true); assert.equal(result.latency,'未测量');
  assert.equal(result.loss,'100.0%'); assert.equal(result.networks[0].latency,'--');
});
test('unsupported targets do not contaminate healthy target RTT or loss', async () => {
  const result = await displayProbe([task([point(3,-1)]),task([point(0,32500)],{id:2,name:'TCP 线路'})]);
  assert.equal(result.latency,'33 ms'); assert.equal(result.loss,'0.0%');
  assert.deepEqual(result.networks,[{name:'ICMP 线路',latency:'不支持'},{name:'TCP 线路',latency:'33ms'}]);
});


test('Emerald touch: ordinary taps navigate; only enabled info taps consume clicks', async () => {
  const { tooltipBehavior } = await import('./emerald-tooltip-fixture.mjs');
  const result = await tooltipBehavior(emeraldSource);
  const closed = { hover: false, open: false, stopped: false };
  assert.deepEqual(result, {ordinary: closed, info: {hover:false, open:true, stopped:true}, cancelled: closed, disabled: closed, mouse: {hover:true, open:false, stopped:false}});
});

test('SNM does not synthesize Swap data that the public protocol does not export', () => {
  const current = toMonitorNode(node);
  assert.equal(current.swap_total, undefined);
  assert.equal(current.metrics.swap_used, undefined);
  assert.ok(toMetricsResponse(node, now).metrics.every(sample => sample.swap_used == null));
});
