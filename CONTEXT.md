# Bannerlord Coop Compatibility

This context describes how BCS Tool makes a third-party Bannerlord campaign module usable with Bannerlord Coop while preserving ownership boundaries between the game, Coop, and the campaign module.

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

## EOE v1.4.7.1 bridge boundary

Static audit found no EOE-owned persistent field in normal campaign play that needs a custom network message:

* `ClansResourceAdder` writes vanilla `Clan.Influence` and `Hero.Gold`; Coop already transports those values. The bridge only prevents duplicate client-side execution.
* `CustomizableClanTier` replaces a vanilla model from pinned configuration and owns no campaign state.
* `BannerColorPersistence` finishes through `Clan.UpdateBannerColor`; Coop's banner handler replays that same call on client mirrors.
* EOE artillery and RF battle AI are mission-host logic over Bannerlord mission objects. They remain owned by EOE/Bannerlord and must be proven through a real Coop battle test, not duplicated by bridge messages.
* EOE custom-battle/debug mode is outside normal Coop campaign scope.

Therefore current bridge performs validation, Coop discovery, invocation scoping, and reversible headless content adaptation only. It contains no campaign seeder and emits no duplicate gameplay state messages.
