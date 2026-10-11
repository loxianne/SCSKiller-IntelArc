# SCSKiller-IntelArc v1.2.5

Unofficial fork based on BlueHeisenberg/SCSKiller with experimental Intel Arc GPU support and Final Fantasy XVI pipeline extraction.

## Changes

### Intel Arc GPU Support (Experimental)
- Tracks DirectX 12 driver cache in Windows' `%LOCALAPPDATA%\D3DSCache` directory.
- Calculates hardware device path hash (`d3ds:<hash>`) to eliminate driver cache misses and promote warmed databases.
- Displays uncapped driver cache limits.

### Final Fantasy XVI Support
- Added native `.pspc` pipeline table parser (`PspcRuns`), extracting all 80,538 author-compiled pipeline configurations directly from Square Enix's shader archives.
- Guarded `PlanBuilder` against combinatorial pairing explosion during pipeline planning.

### Upstream Synchronization
- Merged all changes and optimizations from upstream v1.2.4 and v1.2.5 releases.

### CLI
- Added `scskiller add <path>` and `scskiller remove <id>` commands for manual game library management.

## Installation

1. Download `SCSKiller-IntelArc-v1.2.5-portable.zip` from Assets below.
2. Extract to a local directory and run `SCSKiller.exe`.

### SHA-256 Checksum
```
C844042EB49805CA53671A8E9688BAD396A5792EB3742FF932DD4A7CCFA039AA  SCSKiller-IntelArc-v1.2.5-portable.zip
```

## Compatibility Notes
- **ReShade / OptiScaler:** When external wrappers hook DXGI swapchain slot 10 ahead of SCSKiller, SCSKiller disables frame time monitoring to avoid hook collisions. Pre-warmed shaders and driver caching remain active.
- **Anti-Cheat:** Unofficial builds maintain SCSKiller's strict policy: recorders are never installed into games with active anti-cheat.
