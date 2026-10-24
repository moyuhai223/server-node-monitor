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
test('SNM entry is SDK-only and hash routing preserves root and preview refreshes',async()=>{const api=await readFile(new URL('../web/emerald-snm/src/utils/api.ts',import.meta.url),'utf8');const routes=await readFile(new URL('../web/emerald-snm/src/router/index.ts',import.meta.url),'utf8');const manifest=JSON.parse(await readFile(new URL('../web/emerald-snm/theme.json',import.meta.url),'utf8'));assert.match(api,/createClient\(\{ theme: 'emerald'/);assert.doesNotMatch(api,/fetch\(/);assert.match(routes,/createWebHashHistory\(\)/);assert.equal(manifest.id,'emerald');assert.equal(manifest.sdk,1);});

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
