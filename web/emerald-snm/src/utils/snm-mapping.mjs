// The sole unit conversion boundary: SNM permille / MiB / bytes / Unix ms -> Emerald.
export function probeSummary(probe, now = Date.now()) {
  const points = probe.points || [];
  const measured = points.filter(p => p.state >= 0 && p.state <= 2);
  const ok = measured.filter(p => p.state === 0 && p.us >= 0);
  const last = points.at(-1);
  const stale = !last || now - last.ts > Math.max(45000, (probe.interval || 0) * 2500 + 10000);
  return { samples: measured.length, last, stale, latest: !stale && last?.state === 0 && last.us >= 0 ? last.us / 1000 : null, failure: measured.length ? (measured.length - ok.length) * 100 / measured.length : null,
    average: ok.length ? ok.reduce((sum,p) => sum + p.us / 1000, 0) / ok.length : null };
}
export function toMonitorNode(node, site = {}, now = Date.now()) {
  const l = node.live || {}, mem = site.showSpecs === false ? 0 : (node.memMb || 0) * 1048576, disk = site.showSpecs === false ? 0 : (node.diskMb || 0) * 1048576;
  return {
    id: node.id, name: node.name, sort: node.order, public: true, online: !!l.online,
    country: node.cc || '', group: node.cc || '', last_seen: (l.ts || 0) / 1000,
    last_seen_ago: l.ts ? Math.max(0, (now-l.ts)/1000) : null,
    cpu_cores: site.showSpecs === false ? 0 : node.cores || 0, mem_total: mem, disk_total: disk,
    metrics: l.ts ? { uptime:l.up || 0, cpu:(l.cpu || 0)/10, load:[], mem_total:mem, mem_used:mem*(l.mem || 0)/1000, mem_percent:(l.mem || 0)/10, disk_total:disk, disk_used:disk*(l.disk || 0)/1000, disk_percent:(l.disk || 0)/10, net_rx:l.rx || 0, net_tx:l.tx || 0 } : null,
    os:'', kernel:'', arch:'', virt:'', cpu_name:'', agent_version:'',
    traffic_limit:site.showTraffic === false ? 0 : node.tLimit || 0, traffic_mode:'sum', month_used:site.showTraffic === false ? undefined : l.tUsed,
  };
}
export function toMetricsResponse(node, now = Date.now()) {
  if (!node) return {step:2, metrics:[], ping:[], probes:{}, loss:{}};
  const hist = node.hist || {}, size = Math.max(hist.cpu?.length || 0, hist.mem?.length || 0, hist.rx?.length || 0, hist.tx?.length || 0);
  const metrics = Array.from({length:size},(_,i) => ({ ts:(hist.ts?.[i] || (node.live?.ts || now) - (size-1-i)*2000)/1000,
    cpu:(hist.cpu?.[i] ?? 0)/10, mem_used:(node.memMb || 0)*1048576*(hist.mem?.[i] ?? 0)/1000,
    net_rx:hist.rx?.[i] ?? 0, net_tx:hist.tx?.[i] ?? 0 }));
  const ping = [], probes = {}, loss = {}, probeMeta = {};
  for (const probe of node.probes || []) {
    const stats = probeSummary(probe,now); probes[probe.id] = probe.name;
    probeMeta[probe.id] = { interval:probe.interval || 0, stale:stats.stale, latest:stats.latest, lastState:stats.last?.state };
    if (stats.failure !== null) loss[probe.id] = stats.failure;
    for (const p of probe.points || []) {
      // Unsupported probes aren't failed measurements; failures create null gaps, never 0ms RTT.
      if (p.state < 0 || p.state > 2) continue;
      ping.push({task_id:probe.id,ts:p.ts/1000,latency:p.state===0 && p.us>=0 ? p.us/1000 : null,loss:p.state===0?0:100});
    }
  }
  return {step:2,metrics,ping,probes,loss,probeMeta};
}
