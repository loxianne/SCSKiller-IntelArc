# SCSKiller-IntelArc Release Notes

## SCSKiller-IntelArc v1.2.5-r3

Unofficial fork based on BlueHeisenberg/SCSKiller with experimental Intel Arc GPU support.

### Important Notice on Intel Arc Hardware & 512 MB Driver Cache Container Limit

> Modern Intel Arc graphics drivers enforce an unconfigurable hard ceiling of **512 MB per title** (`%USERPROFILE%\AppData\LocalLow\Intel\ShaderCache\<hash>`).
> 
> Because Intel driver caches are strictly isolated per game container and bounded to 512 MB, games with massive shader libraries or large combinatorial permutation spaces risk saturating the container. Once a game container approaches ~512 MB, the driver's internal LRU eviction queue and bucket fragmentation activate, which can induce micro-stutters and cache thrashing.
> 
> Consequently, **full speculative shader pre-warming on Intel Arc may not work optimally (or at all for very large titles)** without strict pipeline pruning. To prevent container exhaustion and driver stalls, this release enforces **recorded-only compilation by default** for all Intel Arc configurations whenever gameplay recordings exist.

### Key Changes in v1.2.5-r3

#### Compiler & Planner
- **Automatic Recorded-Only Default on Intel Arc:** Compiler planning automatically switches to recorded-only mode (`useRecordedOnly = true`) with **0 speculative permutations** whenever gameplay recording data is present on Intel Arc hardware. This eliminates combinatorial bloat (e.g., pruning 122,000+ speculative permutations down to the ~18,500 real in-game pipelines).
- **Hard Speculative Permutation Cap:** Guarded `PlanBuilder` with a 35,000 maximum speculative permutation threshold on Intel Arc to prevent runaway compile plans from exceeding driver cache limits.
- **Stale Plan Invalidation:** Automatically marks existing speculative plans as stale on Intel hardware when recordings exist, prompting an immediate migration to clean recorded-only plans.

#### User Interface
- **Per-Game Container Gauges:** Refactored cache telemetry across the GUI to reflect Intel Arc's per-title architecture rather than a misleading global limit:
  - **Library Header:** Displays `512 MB limit per game (largest: <size>)`. Warning notices only fire when an individual title's container approaches capacity (>90%).
  - **Library Table:** Hovering over a game's cache size displays exact container capacity, percentage used, and remaining free headroom.
  - **Game Detail Page:** Shows `Intel container (512 MB limit)` with capacity percentage, remaining headroom breakdown, and an interactive utilization `ProgressBar`.

#### Direct3D 12 Proxy & Diagnostics
- **Device Removed Diagnostics:** Added detailed DRED (Device Removed Extended Data) and device removed reason string logging (`DXGI_ERROR_DEVICE_REMOVED`, `DXGI_ERROR_DEVICE_HUNG`, `DXGI_ERROR_DEVICE_RESET`).
- **NVAPI Bypass:** Explicitly bypasses NVAPI query hooks on Intel GPUs (Vendor ID `0x8086`) to avoid foreign driver hook collisions.
- **CSV Telemetry Flushing:** Ensured frame presentation timings are flushed to disk consistently.

---

## SCSKiller-IntelArc v1.2.5-r2

- **Intel Arc Incremental Warm Promotion:** Fixed an issue where staged Direct3D 12 shader caches were only promoted upon 100% completion. Partial compiles (paused, stopped, or yielded) are immediately merged into the target cache database (`f8a99be89df52393`) and usable in-game right away.
- **LocalLow Cache Tracking:** Corrected driver cache directory resolution to `AppData\LocalLow\Intel\ShaderCache`, detecting and reporting native Intel machine-code container slabs.
- **Chained Proxy Telemetry:** Added support for chaining the SCSKiller recorder alongside third-party mod DLLs via `scskiller record alongside on <game>`.

---

## SCSKiller-IntelArc v1.2.5

- **Intel Arc GPU Support (Experimental):** Tracks DirectX 12 driver cache in Windows' `%LOCALAPPDATA%\D3DSCache` directory.
- **Final Fantasy XVI Support:** Added native `.pspc` pipeline table parser (`PspcRuns`), extracting all 80,538 author-compiled pipeline configurations directly from Square Enix's shader archives.
- **Upstream Synchronization:** Merged all changes and optimizations from upstream v1.2.4 and v1.2.5 releases.
- **CLI Commands:** Added `scskiller add <path>` and `scskiller remove <id>` commands for manual game library management.
