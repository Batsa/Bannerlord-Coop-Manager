---
status: accepted
---

# Use Coop's mission seed for EOE battle-scene selection

EOE supplies multiple eligible tactical scenes for many campaign-map terrain indices, while Bannerlord Coop distributes one mission seed but lets every client run Bannerlord's scene lookup independently. For the EOE compatibility recipe, the bridge will implement Deterministic Battle Scene Projection through a Runtime Integration Seam around Coop's `FieldBattleMissionInitializer.Create`, temporarily scoping Bannerlord's native random generator to Coop's existing server-provided mission seed during field-scene resolution and restoring the previous generator afterward. This explicitly approved interception preserves EOE's scene variety, source ordering, and duplicate weighting without modifying EOE or Coop files, changing Coop's protocol or authority, introducing another battle-start message, transferring selection to the Mission Host, or reimplementing Bannerlord's candidate-selection rules.

## Considered Options

- Permanently map each terrain index to one scene: rejected because it discards EOE's battlefield variety.
- Add a bridge message carrying a server-selected scene: rejected because it creates a second, brittle battle-start protocol and new synchronization state.
- Reimplement filtering and choose a slot with a bridge-owned algorithm: rejected because it duplicates Bannerlord behavior and can drift across supported versions.
- Keep Bannerlord's unseeded client-local selection: rejected because clients can choose different scenes for the same battle.

## Consequences

- Participants with Battle Scene Selection Parity resolve the same scene while different battles can still select different EOE scenes. The shared seed and Scene Catalog Parity alone do not correct divergent MapEvent position, map-patch data, naval state, or scene-model filtering.
- The random-generator scope must always be restored, including when field-scene resolution throws.
- Clients hash the complete required battle-scene catalog and asset set during bridge startup, avoiding deferred validation work when a battle opens; the headless server validates only the manifest and catalog material available to it.
- A versioned Battle Scene Catalog Contract pins the known-good catalog and required asset fingerprints for each supported EOE and SandBoxCore combination; package generation cannot silently bless local content drift.
- Catalog or selected-scene asset mismatches use the accepted preview policy: warn and continue, explicitly retaining a residual map-desynchronization risk.
- A parity mismatch writes full client-log details, produces one prominent non-repeating in-game warning when UI is available, and marks every later Battle Scene Resolution Record with `parity=warning`.
- Failure to install the deterministic hook blocks bridge activation. A seed-binding or random-generator restoration failure during invocation aborts the affected mission start; neither path may fall back silently to native client-local randomness.
- Every client emits one structured Battle Scene Resolution Record per field battle containing the MapEvent, seed, local map position and patch data, naval state, scene-model type, map-index-matched contract-candidate count and ordered identifiers, selected scene, catalog-contract hash, and local parity status. These contract fields diagnose parity without claiming to reproduce Bannerlord's private native eligibility filtering.
