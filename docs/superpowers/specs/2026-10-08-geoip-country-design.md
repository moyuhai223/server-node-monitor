# GeoIP country correction

Approved in chat on 2026-10-08. Scope: Master country lookup, cached datasets,
refresh/recalculation, settings source display, deployment documentation and tests.

## Evidence

The current main commit is bbdf0d9. Agent sends addresses; Master looks up the
connection address and exposes `CountryCodeOverride ?? CountryCodeAuto`.
Oracle's public IP ranges list puts 217.142.184.0/21 in ap-singapore-2. The current
npm asn-country dataset returns SE for 217.142.185.22; current server-country,
GeoLite2 and DB-IP datasets return SG. The user's deployed automatic value is US;
its exact historical origin cannot be established without the deployed cache.
The user confirmed the captured IP matches and no manual override is set.

Sources checked on 2026-10-08:
- https://docs.oracle.com/en-us/iaas/tools/public_ip_ranges.json
- https://cdn.jsdelivr.net/npm/@ip-location-db/asn-country/README.md
- https://github.com/sapics/ip-location-db

## Behavior

- Default to GitHub Releases server-country numeric CSVs, retaining offline
  binary-search lookup. No Agent protocol change or additional package dependency.
- Keep the 7-day update interval and 6-hour background check (first after 10 s).
- Support an explicit dataset name and mirror base URL; transparently migrate
  the exact obsolete default npm URL when using the new default dataset.
- Load old asn-country files as a fallback but force refresh even if their
  recorded download timestamp is recent. Identify new caches by dataset and URL.
- Download and validate both families into an isolated generation directory.
  Atomically publish a manifest before switching the in-memory snapshot. Failed
  downloads/validation/cancellation preserve the previous manifest and tables.
- Recalculate all known automatic countries after a successful refresh, including
  previously wrong nonempty countries and offline nodes with a captured address.
  Also reconcile from a current disk cache on startup's first background tick.
- Write only changed automatic-country fields; preserve manual overrides and
  unrelated node edits. Guard writes against changed IPs. Notify existing real-time
  subscribers after changes. Unknown/private addresses retain existing values.
- Keep existing connection-IP selection; proxy/Agent address precedence is outside
  this confirmed fix. Never hard-code the example IP into production logic.
- Display the configured source in the administrator settings. Document migration,
  overrides and mirrors. New default data is PDDL; retain source attribution.

## Verification

Offline fixtures cover the Singapore prefix boundaries, IPv4-mapped IPv6, native
IPv6, private/unknown addresses, legacy migration, source changes, cache reload,
failed/cancelled refresh and malformed ranges. Service/API tests verify existing US
values become SG, overrides survive, notifications fire and repeated reconciliation
is a no-op. Run Master tests, solution tests and the admin production build.
