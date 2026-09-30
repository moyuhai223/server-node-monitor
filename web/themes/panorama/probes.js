// Kept theme-local so the theme remains usable with pre-probe SDK versions.
export function probeStats(probe, now = Date.now()) {
  const points = probe.points || [],
    last = points.at(-1);
  const measured = points.filter((p) => p.state >= 0 && p.state <= 2);
  const success = measured.filter((p) => p.state === 0 && p.us >= 0);
  return {
    last,
    stale:
      !!last && now - last.ts > Math.max(45000, probe.interval * 2500 + 10000),
    failurePct: measured.length
      ? ((measured.length - success.length) * 100) / measured.length
      : null,
    averageMs: success.length
      ? success.reduce((sum, p) => sum + p.us / 1000, 0) / success.length
      : null,
    samples: measured.length,
  };
}
