---
status: accepted
---

# Gate regional population at native family seams

The Player-Regional Party Spawn Policy will use one shared Player-Active Spawn Region and independent, default-off switches for four approved Native Spawn Families: Ambient Outlaws, Villager Trade, Native Settlement Patrols, and Native Battle Deserters. Each enabled family is intercepted at its version-validated systemic campaign-behavior seam and admitted by its native origin semantics. The implementation will not infer policy membership from a party component, template identifier, clan, displayed name, shared factory, or current AI behavior because Bannerlord reuses those mechanisms for unrelated systemic, strategic, and authored encounter parties.

## Considered Options

- Patch shared party factories and classify the resulting party: rejected because deserters and quests reuse looter and bandit factories, and creation has already begun before a safe origin decision can be made.
- Match component types, templates, names, or patrol AI state: rejected because these are not stable ownership boundaries and would capture quest, incident, or mod-authored parties.
- Apply one master switch to every mobile party family: rejected because each family has different origin, lifecycle, gameplay, and fallback rules.
- Suppress all distant villagers without substitution: rejected because it creates distant economic dead zones.
- Convert underway physical villager parties into virtual shipments: rejected because boundary movement could duplicate or lose cargo, income, and return state.

## Consequences

- Ambient Outlaws remain one switch while preserving hideout origins for bandits and town or village origins for looters.
- Villager Trade is a cycle-latched hybrid: an active origin uses the native physical departure path, an inactive origin requires the virtual-shipment path, and an underway physical or virtual cycle finishes in its selected mode before reassessment.
- Native Settlement Patrols use the owning settlement as origin. Leaving all active regions discards the pending patrol timer; re-entry starts a fresh native delay.
- Native Battle Deserters intersect Bannerlord's nearby-village candidates with active regions before random selection. No admitted candidate means no spawn, no global-nearest redirection, and no catch-up work.
- Automatic NPC and player caravans, militia, garrisons, strategic war parties, quest/issue/incident parties, and mod-defined or custom-spawn parties remain outside the initial framework.
- Future custom families require an explicit version-validated adapter declaring their creation seam, origin semantics, ownership, independent switch, and safe missing-origin and no-player behavior.
- All regional settings are restart-only and migrate disabled. Installation is atomic and fails if any enabled family seam cannot be validated; unresolved authoritative data rejects only the affected runtime attempt after successful installation.
- Existing parties are never despawned or converted. Regional Population Policy Telemetry and controlled movement-stage comparisons provide performance evidence without promising a fixed CPU reduction.
