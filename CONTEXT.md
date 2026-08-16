# Bannerlord Coop Compatibility

This context describes how Bannerlord Coop Manager makes a third-party Bannerlord campaign module usable with Bannerlord Coop while preserving ownership boundaries between the game, Coop, and the campaign module.

## Language

**Compatibility Target**:
The installed third-party campaign module and version being evaluated for use with Bannerlord Coop.
_Avoid_: Supported mod, converted mod

**Compatibility Bridge**:
An isolated adapter that represents target-owned data in forms Bannerlord Coop can transport and restores that data into client mirrors without becoming a source of truth.
_Avoid_: Conversion, compatibility patch

**Content Adaptation**:
A reversible representation change that makes existing target content satisfy dedicated-server or Coop input contracts without creating campaign state.
_Avoid_: Campaign repair, content replacement

**Runtime Integration Seam**:
A recipe-scoped, version-validated bridge interception of a Bannerlord, Coop, or Compatibility Target call boundary used to adapt already-owned inputs without changing installed files, network protocol, authority, or gameplay ownership. It must be reversible and fail closed when the proven signature or lifecycle is unavailable.
_Avoid_: Direct mod edit, maintained fork, gameplay override

**Deterministic Battle Scene Projection**:
A per-battle Content Adaptation that uses Coop's shared mission seed and Bannerlord's native selector to resolve one eligible target scene consistently across Client Mirrors with Battle Scene Selection Parity. It preserves target scene variety without making the bridge a scene authority or transferring selection to the Mission Host.
_Avoid_: Fixed scene replacement, client-random scene selection, bridge-owned scene algorithm, Mission Host map selection

**Scene Catalog Parity**:
The condition in which every battle participant exposes the same ordered eligible scene candidates and equivalent scene assets under one Battle Scene Catalog Contract. In preview releases, failed parity is warning-only, so deterministic scene agreement is conditional on parity holding.
_Avoid_: Module-version parity, scene-name parity

**Battle Scene Selection Parity**:
The condition in which every battle participant has the same MapEvent position, map-patch index and coordinates, naval state, active scene-model inputs, and Scene Catalog Parity. Deterministic Battle Scene Projection guarantees one shared scene only while these inputs agree.
_Avoid_: Scene Catalog Parity, shared seed alone

**Battle Scene Catalog Contract**:
A versioned, recipe-owned manifest that defines the known-good ordered scene candidates and required scene-asset fingerprints for supported Compatibility Target and base-game versions. It identifies content without distributing target or TaleWorlds assets or treating an operator's installation as known-good truth.
_Avoid_: Generated local truth, scene-asset bundle

**Battle Scene Resolution Record**:
One structured client-log record per field battle that identifies the MapEvent, Coop mission seed, local map position and patch data, naval state, scene-model type, map-index-matched contract-candidate count and ordered identifiers, selected scene, Battle Scene Catalog Contract hash, and local Scene Catalog Parity status. The contract candidate fields are parity evidence, not a claim that the bridge reproduced Bannerlord's private native eligibility filtering. The record is diagnostic evidence rather than a scene replication message.
_Avoid_: Scene replication message, optional debug trace

**Coop Registration**:
The base Coop process that makes campaign entities which already exist visible to synchronization without creating or altering those entities.
_Avoid_: Party generation, campaign seeding

**Authoritative Campaign**:
The server-side campaign whose state is owned and advanced by Bannerlord and the Compatibility Target.
_Avoid_: Bridge state, synchronized campaign

**Client Mirror**:
A client's local representation of the Authoritative Campaign.
_Avoid_: Client campaign, independent campaign

**Bridge Replication**:
Reading target-owned data from the Authoritative Campaign, carrying it through the Coop session, and applying the same authoritative data to Client Mirrors without inventing game state or game rules.
_Avoid_: State generation, campaign repair

**Campaign State**:
The persistent parties, clans, factions, settlements, encounters, heroes, and related world state owned by the campaign.

**Campaign Seed**:
A normal client-created campaign save imported once to initialize the Authoritative Campaign. After import, the server-owned campaign advances independently; the original client's later local saves are not synchronized back into it.
_Avoid_: Client save synchronization, shared save

**Campaign Intervention**:
Creation, suppression, replacement, or rule-changing mutation of the Authoritative Campaign by a compatibility layer. A Compatibility Bridge does not perform Campaign Intervention.
_Avoid_: Compatibility adaptation, registration

**Server Population Policy**:
An explicitly enabled, server-wide operator rule that changes future Authoritative Campaign party population independently of Compatibility Target behavior. It may share runtime packaging with a Compatibility Bridge but is not Compatibility Bridge behavior.
_Avoid_: Compatibility feature, recipe fix

**Player-Regional Party Spawn Policy**:
An operator-enabled, restart-bound Server Population Policy framework that admits future Bannerlord-owned spawn attempts by Native Spawn Family and Player-Active Spawn Region. Every supported family has an independent enable switch; a disabled family retains fully native behavior. All family switches default off, and older configuration schemas migrate with every regional family disabled. The shared radius and family switches are loaded only at server startup; runtime changes take effect after restart so patch lifecycle, pending patrol timers, and in-flight villager cycles cannot transition partially. Installation is atomic: if any enabled family's exact native method or required field cannot be validated and patched, population-control startup fails rather than silently disabling that family or applying only part of the requested policy. After successful installation, missing authoritative player or origin data rejects only the affected runtime attempt. Families share region calculation but retain their own native timing, chance, caps, origin selection, and creation flow. Existing parties remain untouched. Automatic NPC caravans are not a supported regional family: they retain the existing per-town, global-cap, capacity, budget, and destination-balancing controls, while player-clan caravans remain native. Settlement-resident militia and garrison parties are also excluded because they are authoritative defender state rather than roaming ambient population. Strategic war parties—including lord, clan, rebel-clan, minor-faction, and army-member parties—remain native because their creation participates in inseparable campaign transitions such as clan creation, war, rebellion, ownership, and army lifecycle. Quest-, issue-, and incident-authored parties remain native regardless of component or factory reuse; regional controls intercept only approved systemic campaign behaviors and never a shared party factory globally. Mod-defined and custom-spawn parties are unsupported initially. A future custom family requires a version-validated adapter that declares its exact creation seam, native origin semantics, gameplay ownership, independent switch, and safe no-origin and no-player behavior; component type or template-name matching alone is insufficient.
_Avoid_: Global party filter, bandit-only region gate, compatibility adaptation

**Native Spawn Family**:
A semantically related group of parties defined by one Bannerlord-owned creation flow and its origin rules, not merely by PartyComponent type, template identifier, clan, or displayed name. Each supported family is admitted independently because different gameplay systems may reuse the same component or factory.
_Avoid_: Party component category, template-name match

**Regional Ambient Outlaw Spawn Policy**:
The combined bandit-and-looter Native Spawn Family configuration of the Player-Regional Party Spawn Policy. When independently enabled, it permits new looter and bandit spawn attempts only from eligible origin sites within a Player-Active Spawn Region. Bandits and looters share one switch because they participate in Bannerlord's systemic outlaw population behavior, while their distinct native origins remain preserved. Admission creates no immediate or accumulated attempt; Bannerlord's native timing, chance, global population caps, and relative preference among admitted Native Bandit Spawn Origins remain authoritative. Existing parties remain untouched.
_Avoid_: Bandit initialization, EOE bandit fix, compatibility adaptation

**Regional Villager Trade Policy**:
The villager Native Spawn Family configuration of the Player-Regional Party Spawn Policy. Enabling it requires the virtual-villager shipment capability; invalid configuration must be rejected rather than silently suppressing distant trade. At each native departure opportunity, an origin village inside a Player-Active Spawn Region may use Bannerlord's physical villager path, subject to the remaining native and operator population rules; an origin outside every active region uses the existing virtual-villager shipment path instead of starting a physical journey. The selected physical or virtual mode is latched for that complete outbound-and-return trade cycle and is reassessed only at the next native departure opportunity. Region changes never convert an underway physical party or virtual shipment, and the two modes never run concurrently for one village cycle. Virtual cargo, cooldown, and travel-time tuning remains independently configurable while the capability is required.
_Avoid_: Mid-route virtualization, duplicate villager delivery, proximity-based despawn

**Regional Settlement Patrol Spawn Policy**:
The settlement-patrol Native Spawn Family configuration of the Player-Regional Party Spawn Policy. It applies only to parties created by Bannerlord's settlement-patrol campaign behavior and evaluates the owning settlement as the native origin. It does not classify parties by a generic patrol AI state or include mod-defined parties merely configured to patrol. Bannerlord remains authoritative for guard-house eligibility, spawn duration, template, land or port origin, and the one-patrol-per-settlement rule. A settlement leaving every Player-Active Spawn Region loses any pending patrol-generation time; re-admission starts a fresh native spawn duration and cannot produce a deferred or catch-up patrol.
_Avoid_: Patrol-behavior filter, custom patrol template gate

**Regional Battle Deserter Spawn Policy**:
The battle-deserter Native Spawn Family configuration of the Player-Regional Party Spawn Policy. It applies only to parties created by Bannerlord's deserter campaign behavior from routed and dead troops after ordinary map battles. Regional admission first preserves Bannerlord's nearby-village candidate search, then intersects those candidates with the Player-Active Spawn Region before native random selection. If no candidate remains, the attempt creates no deserter party; it does not redirect the battle outcome through Bannerlord's global nearest-village fallback and creates no deferred or catch-up work. The policy does not treat all parties made through looter or bandit factories as deserters. Quest-authored deserter, bandit, and encounter parties remain untouched even when they reuse the same factories.
_Avoid_: Quest deserter gate, looter-component deserter classification

**Player-Active Spawn Region**:
The union of circular regions defined by a configurable straight-line campaign-map radius, expressed in average bandit travel-days, around each connected player's registered party after that party resolves to a live Authoritative Campaign entity. Settlement, siege, encounter, battle, or visual state does not remove the region while its authoritative campaign location remains available; lobby-only, unresolved, and disconnected players create no region. An admitted party may initially appear outside the radius and may subsequently roam beyond it.
_Avoid_: Exact spawn radius, party confinement area

**Regional Population Policy Telemetry**:
A startup configuration record, actual error records, and one aggregate summary per campaign day for the Player-Regional Party Spawn Policy. The daily summary reports, per enabled family, native attempts evaluated, physical attempts admitted, attempts suppressed outside active regions, and attempts rejected for missing authoritative player or origin data. It additionally reports villager virtual cycles scheduled and pending, patrol timers discarded, and deserter groups suppressed. Successful individual attempts do not emit log records.
_Avoid_: Per-spawn success log, profiler substitute

**Regional Population Performance Evidence**:
A controlled comparison using the same campaign save, authoritative player locations, campaign-speed window, and enabled-family combination. It correlates supported-family moving-party counts and physical-versus-virtual villager cycles with movement-data filling, distance-calculation, moving-party tick, movement-application, and overall campaign tick or load timing. Acceptance requires a clear downward trend plus functional near-player native behavior and village trade outcomes; it does not promise a fixed CPU percentage.
_Avoid_: Uncontrolled log comparison, fixed performance guarantee

**Native Bandit Spawn Origin**:
The Bannerlord-owned geographic source for a bandit-aligned spawn attempt: a hideout selected under native culture and infestation preferences for a bandit party, or a town or village for a looter party. Regional admission may exclude an origin but does not substitute one origin type or replace native fallback behavior.
_Avoid_: Hideout for all bandits, generated spawn point

## Generic compatibility boundary

The bridge package format is target-neutral. A compatibility recipe owns the
target identity, exact supported versions, reversible content adaptations,
bridge rules, runtime features, lifecycle metadata, and optional UI guidance.
Schema 2 enables only the runtime features declared by that recipe; a generic
bridge declares none. Schema 1 remains readable with its historical all-feature
behavior solely for compatibility with already-generated bridge packages.

Adding a target therefore means registering a reviewed recipe and its proven
feature set. It does not mean adding unconditional target behavior to the
shared runtime. A recipe may use a Runtime Integration Seam, including a narrow
Coop call boundary, only to adapt existing synchronized inputs without changing
Coop's files, protocol, authority, or gameplay ownership. Generic analysis still
fails closed when executable campaign code needs compatibility that has not
been proven.

## EOE v1.4.7.1 bridge boundary

Static audit found no EOE-owned persistent field in normal campaign play that needs a custom network message:

* `ClansResourceAdder` writes vanilla `Clan.Influence` and `Hero.Gold`; Coop already transports those values. The bridge only prevents duplicate client-side execution.
* `CustomizableClanTier` replaces a vanilla model from its module configuration and owns no campaign state.
* `BannerColorPersistence` finishes through `Clan.UpdateBannerColor`; Coop's banner handler replays that same call on client mirrors.
* EOE artillery and RF battle AI are mission-host logic over Bannerlord mission objects. They remain owned by EOE/Bannerlord and must be proven through a real Coop battle test, not duplicated by bridge messages.
* EOE custom-battle/debug mode is outside normal Coop campaign scope.

Therefore current bridge performs validation, Coop discovery, invocation scoping, and reversible headless content adaptation only. It contains no campaign seeder and emits no duplicate gameplay state messages.
