# Bannerlord Coop Manager

Bannerlord Coop Manager is a Windows desktop manager for a Bannerlord Coop
dedicated server. The executable and user interface use the
**Bannerlord Coop Manager** name.

This repository publishes a **preview Windows build**. It extends the upstream
BCS Tool codebase
with dedicated-server module management, reversible Coop bridge installation,
client bridge packaging, client-save import, persistent server
logging, and a version-scoped compatibility path for **Empires of Europe 1700
(EOE)**.

> Latest downloadable application: `0.3.0-beta.7` (generated bridge `0.6.73`)
>
> Current source-generated bridge runtime: `0.6.73` (included in this download)
>
> Upstream base: [`AppleDeath318/BCSTool@f7bc05c`](https://github.com/AppleDeath318/BCSTool/commit/f7bc05c672dad169663f9c8b245e5b01b5422742)

## Status at a glance

The EOE work is promising but is **not yet a stable or general compatibility
release**.

| Area | Current status |
|---|---|
| GUI server lifecycle, configuration, logging, and console | Implemented and regression-tested |
| Dedicated-server module profile and enforced UDP port `4200` | Implemented and regression-tested |
| Client campaign import into the server | Implemented and regression-tested |
| Version-scoped server/client bridge generation | Implemented and regression-tested |
| EOE `1.4.7.1` dedicated-server bridge installation | Version/file/signature validated implementation |
| EOE server startup and client join | Demonstrated in the prior hand test |
| EOE 1696x1696 map/weather correction | Hand-tested; observed weather/MapEvent index failures stopped |
| Ordinary Coop battles | Multiple battles completed in the prior hand test |
| Bridge `0.6.73` recipe-scoped runtime features, deterministic EOE battle-scene projection, per-DLL selection, character-creation lifecycle repair, shared economy save-schema registration, overlays, registry/population/workshop-cache fixes, EOE inherited-shield production suppression, and semantic runtime-version compatibility | Built and regression-tested; live hand retest pending |
| Optional caravan/villager/bandit population, regional spawning, and economy controls | Implemented server-side with default-off regional families; live campaign/performance calibration pending |
| World-map client movement | Still under investigation; teleporting/stalls were observed |
| Save/reconnect, late join, and long-duration acceptance | Not yet proven on the final build |

The manager can statically analyze other modules, but that is not a promise that
they synchronize correctly. EOE is the current compatibility target. Realm of
Thrones and arbitrary executable modules must be treated as experimental until
they receive their own complete runtime test matrix.

## Supported development snapshot

The current EOE path was developed and tested against:

| Component | Version |
|---|---:|
| Mount & Blade II: Bannerlord client | `1.4.8` |
| Bannerlord Coop | `0.1.2` |
| Empires of Europe 1700 | `1.4.7.1` |
| Dedicated-server game runtime | `1.4.8` |
| Source-generated bridge | `0.6.73` |

These are supported compatibility versions, not floating minimum versions. The
bridge installation validates versions, required files, paths, assembly identities,
and required method signatures. It does not reject compatible files merely
because their bytes or hashes differ. A game or Workshop update requires a new
**Prepare / Install Bridge → reinstall client ZIP** cycle.

The current EOE server profile does **not** require separate Harmony, ButterLib,
UIExtenderEx, or Mod Configuration Menu modules. Coop supplies the Harmony
runtime used by the bridge. StoryMode is not enabled as a server module and is
not a bridge manifest dependency; bridge installation only locates the
official local `StoryMode.dll` needed by EOE artillery code. That official DLL
is never redistributed by this project.

## Requirements

- Windows 10 or Windows 11, x64
- A full Steam installation of Mount & Blade II: Bannerlord `1.4.8`
- Bannerlord Coop `0.1.2`, including its dedicated-server package
- Empires of Europe 1700 `1.4.7.1`
- Steam Workshop updates completed before bridge installation
- UDP port `4200` available; direct Internet hosting normally requires an
  inbound firewall rule and router port forward
- The same Bannerlord, Coop, EOE, and generated bridge versions on every client

Bannerlord Coop and EOE are not bundled with this repository.

## Download, extract, and run

No installer, .NET SDK, or source build is required.

1. Open [GitHub Releases](https://github.com/Batsa/Bannerlord-Coop-Manager/releases).
2. Under the newest release's **Assets**, download the file named like
   `Bannerlord-Coop-Manager-v0.3.0-beta.7-win-x64.zip`. Do not download the
   automatically generated **Source code** archives.
3. Right-click the downloaded ZIP and select **Extract All**. Extract it to a
   normal writable folder, such as `Documents\Bannerlord Coop Manager`.
4. Open the extracted folder and run `Bannerlord Coop Manager.exe`.

Keep every extracted file together. In particular,
`BCSTool.RuntimeBootstrap.dll` must remain beside `Bannerlord Coop Manager.exe`; it is used
when the manager starts a modded dedicated-server profile.

The preview executable is self-contained for Windows x64 but is not digitally
signed. Windows may therefore show an **Unknown publisher** or SmartScreen
warning. Verify the ZIP against its adjacent `.sha256` release asset before
running it. `START-HERE.txt` inside the ZIP repeats these instructions.

The ZIP contains only Bannerlord Coop Manager. Bannerlord, Bannerlord Coop,
EOE, and the dedicated-server package must already be installed separately.
The current `0.3.0-beta.8` download generates bridge `0.6.73`.

## Initial server setup

1. Install or update Bannerlord, Bannerlord Coop, and EOE to the exact versions
   above.
2. Exit Bannerlord and stop the dedicated server completely.
3. Launch `Bannerlord Coop Manager.exe`.
4. Confirm that Bannerlord Coop Manager detected `BannerlordCoopServer.exe`. Use **Browse** if
   it did not.
5. If Coop has not created its configuration files yet, perform one initial
   server boot, let Coop create them, then stop the server.
6. Open **Server Configuration** and review the campaign name, autosave,
   password, Steam, and logging settings.
7. Keep `traceTick`, `tracePublish`, and `traceBandits` off unless collecting a
   targeted diagnostic.

Coop configuration paths:

```text
%USERPROFILE%\Documents\Mount and Blade II Bannerlord\CoopData\mod-config.json
%USERPROFILE%\Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer\server-config.json
```

Bannerlord Coop Manager preserves the existing JSON-with-comments layout where supported and
creates a sibling `.bak` before replacing a configuration file.

## Prepare a supported overhaul mod

For the mod-list portion of setup, the complete supported workflow is exactly:

1. Stop the server completely.
2. Open **Server Mods**.
3. Drag the supported overhaul's module folder—the folder containing
   `SubModule.xml`—onto the module list.
4. Optional: select the overhaul and click **Bridge DLL Options**. Every DLL
   actively declared in its `SubModule.xml` is selected on a fresh install; use
   individual checkboxes, **Select All**, or **Clear All**.
5. Click **Prepare / Install Bridge**.

The drop itself is consent to import the server copy. The button is
consent to perform the backed-up bridge operation. There are no routine
confirmation dialogs and no required DLL-options, Analyze, reorder, or Save Load
Order step. On success the Server Mods window closes, the main status reports
readiness, the Bannerlord Coop Manager Console records the matching client ZIP path, and the
next server action can be **Start** after the campaign save is selected/imported.

The same bridge operation owns EOE DLL
projections under `Europe1700\bin\Win64_Shipping_Server`, the
`conf_clans_resource_adder.xml` projection beside `ClansResourceAdder.dll`,
headless XML overlays, the server load-order profile, the generated bridge
module, and its matching client ZIP. Start runs the same recipe as a preflight
  and repairs missing bridge-owned projections with the installed DLL selection
  before launching Bannerlord.
Existing module folders are never silently replaced. For a clean or freshly
created server Modules directory, the supported overhaul setup remains exactly
the two actions above.

The UI is recipe-driven. EOE is the first built-in, tested recipe; a future
overhaul receives the same two-action lifecycle only after a specific bridge
recipe has been supplied and registered. Static generic analysis is not treated
as a compatibility recipe.

Target identity, supported versions, preparation logic, bridge rules, runtime
features, lifecycle metadata, and optional population guidance are registered
as one recipe. Generic executable targets still receive the conservative
manifest/bridge projection, but their schema-2 bridge configuration declares no
target-specific runtime features. EOE's recipe explicitly declares its reviewed
runtime features. Existing schema-1 bridge packages remain accepted with only
their historical hook set; later features are never enabled implicitly.

The resulting enabled order is:

```text
Native
DedicatedServer.Windows
SandBoxCore
Sandbox
Coop
Europe1700
BCS.CoopBridge.<identity>
```

The bridge loads last. Its manifest dependencies are exactly Coop and
Europe1700.

Bridge installation can create or update:

```text
<DedicatedServer>\bcs-server-modules.json
<DedicatedServer>\bcs-client-packages\BCS.CoopBridge.<identity>.zip
<DedicatedServer>\bcs-compatibility-backups\<plan-id>\
<DedicatedServer>\engine\Modules\BCS.CoopBridge.<identity>\
```

The Steam Workshop EOE source and protected Coop files are untouched. The
imported dedicated-server EOE copy receives backed-up manifest/headless-file
transformations; bridge-owned overlays and projections are recorded in the backup manifest.
**Revert Bridge Install** restores the latest recorded installation.

Every DLL actively declared by the selected overhaul is available in **Bridge
DLL Options**, including `RF_BattleAI.dll` and `EOE.CustomBattlePatch.dll` when
those declarations exist, and all declarations are selected by default. Disabled DLLs are omitted from bridge `MODULE`
requirements and server projection, and their server declarations receive the
dedicated/no-render suppression tags. The exact selection is part of the bridge
identity and generated client package. Reinstalling with a different selection
therefore creates a different bridge ID, and the normal backup/revert operation
restores the prior manifest and bridge configuration.

EOE's selected `BannerColorPersistence.dll` keeps its existing client-only role:
it remains included for clients and suppressed on the dedicated server. Clearing
its checkbox turns it into a global exclusion like any other DLL.

The client ZIP intentionally does not overwrite a Workshop mod's
`SubModule.xml`. Before launching each client, remove or comment the same
disabled DLL declarations in that client's mod manifest. Bridge `0.6.73` checks
this policy on both roles and rejects an active mismatch; because the target mod
loads before the bridge, that check cannot undo behavior from a DLL that the
client already started.

### Optional bridge population and trade settings

After installing the bridge, reopen **Server Mods**, click the enabled current
`BCS.CoopBridge.<identity>` row, and select **Bridge Population Settings**.
The editor can set:

- a soft global maximum for future automatically created NPC caravans;
- automatic NPC caravans per town, defaulting to `2`;
- NPC caravan inventory-capacity and new-caravan trade-budget multipliers;
- a capped destination-age bonus that raises the native score of towns that
  have gone longer without an NPC caravan visit;
- a soft maximum for active villager trade parties;
- physical villager capacity plus optional virtual cargo, cooldown, and travel
  time controls for native villager departures rejected by that ceiling; and
- a multiplier for regular bandit parties spawned around hideouts;
- one shared player-active radius in average bandit travel-days; and
- independent, default-off regional switches for ambient outlaws, villager
  trade, settlement patrols, and post-battle deserters.

The per-town caravan ceiling defaults to `2`; the global caravan and villager
ceilings default to native behavior. Player-clan caravans are excluded from both
bridge caravan ceilings. These controls never delete existing parties from an
imported save; global or per-town counts above a ceiling decline only through
normal campaign attrition. Capacity scaling affects eligible active parties
after restart. Trade-budget scaling applies only when a new automatic NPC
caravan is initialized; existing caravan gold is not rewritten. Lower villager
limits can reduce food and trade delivery and should be changed cautiously.
Changes are read on the next server start.

Regional spawning is a server-wide operator policy, not an EOE recipe fix. It
uses the union of valid authoritative campaign positions for players whose Coop
connection is in campaign or mission state. Lobby, loading, unresolved, and
disconnected players create no region. Enabled families retain Bannerlord's
native timing, chance, caps, templates, and creation behavior; the bridge only
filters reviewed origin-selection seams. Existing parties, caravans,
militia/garrisons, strategic war parties, quest/issue/incident parties, and
mod-defined/custom spawns are untouched. Regional villager trade requires the
virtual-shipment capability and latches physical versus virtual mode for the
whole trade cycle. Leaving a patrol region discards its pending timer; re-entry
starts a fresh native delay; queued patrol origins are rechecked every campaign
quarter-hour. Deserters use only native nearby villages that
remain inside a region and never fall back to a global nearest village.

All four regional switches default off. V1 and V2 population files migrate with
the regional policy disabled and a `0.5` travel-day radius. Enabling any family
is restart-bound and atomic: an exact Bannerlord or Coop ABI mismatch blocks
population-control startup instead of leaving a partially installed policy.
Startup and daily aggregate telemetry is written without per-spawn success
noise.

All economy controls migrate with no-effect defaults: capacity, budget, cargo,
and travel-time multipliers are `1`; the destination-age maximum bonus is `0`;
and virtual villager shipments are disabled. A destination-age bonus of `1`
allows a valid native town score to rise gradually to at most `2x` over the
configured horizon. It never makes an invalid, hostile, or otherwise rejected
destination eligible.

Virtual shipments do not mint goods. When Bannerlord attempts a native village
departure that the configured global villager ceiling rejects, the bridge can
reserve real village stock and that village's current trade-bound destination,
then schedule an outbound and return journey. At arrival it sells only that
reserved manifest subject to town gold; on return it restores unsold cargo and
applies Bannerlord's village-income tax model. Unsafe, raided, or besieged
endpoints defer delivery; cargo returns unsold if the pinned destination becomes
hostile in transit. The virtual path intentionally has no map party, battle,
interception, escort, or
quest interaction, so it is an economic approximation rather than a simulated
party. Pricing uses an active villager trade party when one exists. If none
exists, the bridge logs once and uses Bannerlord's null-party price fallback,
so income and village tax can differ from a physical delivery.

For the EOE bridge, the editor reads the installed overhaul's settlement data
and shows an advisory native-scale guide beside both limits. Bannerlord normally
targets two merchant caravans per non-castle town and one active villager party
per village. The editor also multiplies the detected town count by the selected
per-town value, so EOE's 236 towns show targets of 472 at the default or 236 at
one caravan per town. These calculated values guide custom limits; they are not
hard engine maxima, and imported or customized campaign state can exceed them.
For the current 236-caravan/400-villager EOE profile, `2x` caravan capacity and
`2x` new-caravan budget are reasonable owner-controlled starting values when
testing one caravan per town. They are not guaranteed economic equivalence.
Virtual shipments should be enabled separately and calibrated from campaign
food, market stock, workshop input, and prosperity outcomes to avoid compounding
the physical-party multipliers.

The server-only values are stored at
`<DedicatedServer>\bcs-coop-bridge-population.config` and
`<DedicatedServer>\bcs-coop-bridge-economy.config`. They survive bridge
updates, are not included in the client ZIP, and do not change the bridge ID.
The population file now uses strict schema v3. Schema-v1 population files retain
their existing global ceiling and receive the per-town default of `2`; schema-v2
files retain all four legacy controls; both migrate with every regional family
off. The separate economy sidecar uses strict schema v1, and a missing sidecar
means neutral economy defaults.
Coop remains the single owner of its existing looter multiplier and
wanderer/companion limits in **Mod Configuration**.

### What the EOE bridge installation currently addresses

- The dedicated server's incorrect 848x848 terrain size for EOE's 1696x1696
  world map
- Delayed client handler discovery so the bridge does not hard-reference
  Coop's `Common.dll` before Coop loads
- Headless action/action-type and malformed trebuchet XML adaptation
- Reversible server `Items.xsd` support for EOE firearm alternate Weapon modes;
  EOE's `items_guns.xml` remains unchanged
- Exact workshop ranged-tier repair plus a live-item category-cache repair
- Invalid Bearskin Cape references and legacy civilian equipment attributes
- Direct XSLT-load redirection through Bannerlord's required `ApplyXslt` path
- Narrow client MapEvent removal authority and troop-upgrade after-load repair
- Deterministic EOE field-battle scene selection: every client scopes
  Bannerlord's native scene selector to Coop's existing shared mission seed,
  preserving EOE's ordered scene variety and duplicate weighting
- Server `MBSaveLoad.CurrentVersion` recovery from the exact observed server
  runtime version when Bannerlord's virtual file system returns an empty value
- Deterministic loaded-Army identities, nullable Army AI targets, and deferred
  PartyComponent links replayed before Coop publishes registry readiness
- Bounded diagnostics for disorganization and missing TroopRoster sequencing

The bridge does not invent campaign state and does not replace Bannerlord or
Coop as the campaign authority.

## Install the matching client bridge

Every client needs the ZIP generated by the same bridge installation:

```text
<DedicatedServer>\bcs-client-packages\BCS.CoopBridge.<identity>.zip
```

1. Exit Bannerlord.
2. Extract the ZIP at the Bannerlord installation root so its included
   `Modules` directory merges with the game's existing `Modules` directory.
3. Do not extract it into `Modules` if that would create
   `Modules\Modules\...`.
4. Remove or disable older `BCS.CoopBridge.*` module IDs.
5. Enable the matching Coop, Europe1700, and generated bridge modules in this
   order:

```text
Coop
Europe1700
BCS.CoopBridge.<identity>
```

If an installed EOE copy is replaced or updated outside Bannerlord Coop Manager, click
**Prepare / Install Bridge** again. If Coop or the game updates, click the same
button and distribute the newly
generated ZIP. Bannerlord Coop Manager does not download or silently update Workshop mods.
The ZIP contains the bridge runtimes, manifest, configuration, pinned EOE
battle-scene catalog contract, GPL notice, and attribution. At startup, the
client verifies the catalog and required scene assets before a battle can open.
Contract-integrity or hook-installation failures stop bridge activation. Local
EOE/SandBoxCore scene drift currently warns once in game and records full log
details, but does not block play; continuing after that warning retains a map
desynchronization risk. Server-only transformed EOE XML/XSLT overlays stay on
the server and are not redistributed in the client ZIP.

## Seed the server from a client campaign

The server needs an existing EOE campaign. Character creation remains a normal
Bannerlord client task:

1. Start a normal Bannerlord Sandbox game with the exact EOE build.
2. Create the character and campaign.
3. Save manually.
4. Exit Bannerlord completely so the save is stable.
5. In Bannerlord Coop Manager, open **Server Configuration** and click **Refresh Saves**.
6. Select the campaign under **Client campaign**.
7. Click **Import Client Save**.
8. Click **Save** or **Save & Close** so the imported name becomes the active
   server campaign.

Source and destination:

```text
%USERPROFILE%\Documents\Mount and Blade II Bannerlord\Game Saves
%USERPROFILE%\Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer\Game Saves
```

Import copies the `.sav` through a staging file, verifies SHA-256, and never
overwrites an existing server save or unrelated sidecar. Coop creates the
server-side `.json` sidecar after hosting the imported campaign.

On the first Coop join, that sidecar has no player identity yet. Bannerlord
plays the campaign intro before opening character creation; with EOE this can
take about three minutes and may resemble a static loading screen. Wait for it
or press Esc once to skip it safely, keep the client open, complete and confirm
character creation, and then wait for the server save transfer. Bannerlord Coop Manager warns
about this before starting a campaign
whose sidecar is missing or has an empty `Players` array.

For current operation, use only ASCII letters, digits, and underscores in a
campaign name, without `.sav`; for example, `EOE_Test4`. Avoid spaces and
special characters even though Windows itself permits some of them.

Import is a one-time seed. Once hosted, the server copy is the authoritative
campaign. A client's later local saves are not synchronized back into it.

## Start and connect

1. Confirm all server modules and the active save.
2. Click **Start**.
3. Wait for the server to report that it is serving and waiting for clients.
4. Connect through the supported Coop flow or directly to the host on UDP
   `4200`.
5. For a newly imported campaign, wait through the first-join intro and finish
   character creation. Save transfer begins only after character creation is
   confirmed.

BCS-managed module-profile launches invoke the engine with:

```text
/dedicatedcustomserver 4200 EU 0
```

Port `4200` is enforced by the managed launch plan; it is not read from
`server-config.json`. If no `bcs-server-modules.json` exists, Bannerlord Coop Manager uses the
upstream unmanaged launcher path instead.

## Logs and diagnostics

Enable `logFile` in **Server Configuration**. Managed launches persist the
complete UTF-8 ConPTY character stream, including ANSI/VT control sequences,
under:

```text
%USERPROFILE%\Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer\logs
```

Files use this form:

```text
coop-server-yyyyMMdd-HHmmss[-N].log
```

Segments roll near 64 MiB and the ten newest files are retained. Output is not
deduplicated, filtered, or truncated.

Bannerlord Coop Manager's own lifecycle log is stored under:

```text
%LOCALAPPDATA%\BCSServerTool\Logs\BCSTool-yyyy-MM-dd.log
```

Before sharing logs, remove server passwords, public addresses, Steam IDs, and
player-identifying information.

## Coop backups, bridge rollback, and safety

- Stop Bannerlord and the server before campaign import, bridge installation, or
  revert.
- Bridge installation re-checks inputs before writing and aborts if anything changed
  while its installation plan is being applied.
- **Revert Bridge Install** uses the recorded backup manifest; do not manually
  edit that manifest.
- Do not manually copy Harmony, Coop, game, or mod DLLs into the dedicated
  server's engine root.
- Bannerlord Coop owns campaign-save rotation and retains two paired native
  generations: `<name>.backup1.sav/.json` and `<name>.backup2.sav/.json`.
- Bannerlord Coop Manager does not create a second campaign-save rotation or restore those
  native generations. Existing `Game Saves\BCS Backups` folders from older BCS
  Tool builds are preserved as legacy data and are not deleted automatically.
- Bannerlord Coop Manager still creates narrow safety copies for its own configuration/profile
  writes and reversible bridge-install manifests under
  `bcs-compatibility-backups`; these are not campaign-save backups.

## Known limitations

- Client movement on the world map was still teleporty, sticky, or laggy in
  the last hand test. New diagnostics can determine whether party
  disorganization or missing TroopRoster registration contributes, but no
  speculative movement rewrite has been added.
- The `0.6.73` bridge targets proven server `Failed to get ID` roots: orphaned
  load-time party visuals, headless map-event visuals, deterministic loaded-Armies,
  nullable Army targets, pre-registration PartyComponent links, and synthetic
  workshop warehouse-roster copies. The previous log also contained a separate
  daily ItemRoster family that cannot be identified from that log alone. The
  bridge now records the missing roster's owner and synchronous caller for up to
  32 unique rosters while leaving Coop's original error visible and behavior
  unchanged; the next hand test is required before applying an owner-specific fix.
- Final-build save/reconnect, late join, long-duration synchronization, and
  repeated battle acceptance are not complete.
- Large EOE campaign saves around 100 MiB previously caused multi-second
  synchronous save stalls. Measured join data was about 102.4 MiB raw and
  10.5 MiB compressed on the wire, plus a 1.2 MiB one-time party baseline.
- EOE contains 1,628 total settlements (236 towns), not 1,628 towns. The last
  test logged 4,762 heroes and 3,042 parties; a claim of more than 2,000 lords
  was not confirmed. Sustained Play_1x CPU/network saturation still needs a
  timed live capture because the measured client session remained paused.
- EOE firearm alternate melee modes remain intact; bridge installation validates
  the complete `items_guns.xml` against its reversible server schema correction.
- Manual pause events are not currently classified as server failures.
- EOE/RF combat AI behavior is owned by EOE and is outside this manager's fix
  scope.

## Building from source

### Prerequisites

- Visual Studio 2026 or Build Tools with **.NET desktop development**
- .NET 10 SDK
- .NET 6 SDK/reference pack, used to compile the dedicated-server startup hook
- Windows PowerShell

From the repository root:

```powershell
dotnet restore .\BCSTool.sln
dotnet build .\BCSTool.sln -c Release --no-restore
dotnet run --project .\BCSTool.RegressionTests\BCSTool.RegressionTests.csproj -c Release --no-build
```

The regression runner currently contains 65 named checks and finishes with:

```text
All Bannerlord Coop Manager regression checks passed.
```

### Publish the application

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\BCSTool\Publish-SingleExe.ps1
```

Publish output:

```text
BCSTool\publish\Bannerlord Coop Manager.exe
BCSTool\publish\BCSTool.RuntimeBootstrap.dll
BCSTool\publish\START-HERE.txt
BCSTool\publish\README.md
BCSTool\publish\LICENSE
BCSTool\publish\NOTICE.md
```

The executable is self-contained for Windows x64. Keep the bootstrap DLL next
to it. The other files provide install guidance, licensing, and attribution.

### Rebuild the bridge payloads

The application embeds distinct server and client bridge assemblies. They are
compiled from `BCSTool.CoopBridgeArtifact` against locally installed Bannerlord
and Coop assemblies; those third-party references are not in this repository.

Example server build:

```powershell
dotnet build .\BCSTool.CoopBridgeArtifact\BCSTool.CoopBridgeArtifact.csproj `
  -c Release `
  -p:BridgeTarget=Server `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -p:BannerlordBin="<DedicatedServer>\engine\bin\Win64_Shipping_Server" `
  -p:CoopBin="<DedicatedServer>\engine\Modules\Coop\bin\Win64_Shipping_Server"
```

Example client build:

```powershell
dotnet build .\BCSTool.CoopBridgeArtifact\BCSTool.CoopBridgeArtifact.csproj `
  -c Release `
  -p:BridgeTarget=Client `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -p:BannerlordBin="<Bannerlord>\bin\Win64_Shipping_Client" `
  -p:CoopBin="<Coop module>\bin\Win64_Shipping_Client"
```

Client compilation targets .NET Framework 4.7.2 and therefore also requires
the corresponding reference assemblies/developer pack. After replacing the
two embedded payloads, update their integrity hashes in
`CoopBridgePackageBuilder.cs` and run the entire regression suite. A changed
payload intentionally changes the generated bridge identity.

## Repository layout

```text
BCSTool/                       WPF manager application
BCSTool.RuntimeBootstrap/      managed dedicated-server assembly resolver
BCSTool.CoopBridgeArtifact/    server/client compatibility bridge source
BCSTool.RegressionTests/       deterministic regression and ABI checks
scripts/                       opt-in local runtime and smoke-test tools
CONTEXT.md                     compatibility-domain glossary
NOTICE.md                      provenance and attribution
LICENSE                        GNU GPL version 3
```

Runtime smoke scripts require explicit local Bannerlord, Coop, EOE, and server
paths. They are diagnostic tools and may start or stop game/server processes;
read each script before running it.

## Attribution

Bannerlord Coop Manager is a modified derivative of
[**BCS Tool**](https://github.com/AppleDeath318/BCSTool), originally created by
**AppleDeath** (`AppleDeath318`). This work is based on upstream commit
`f7bc05c672dad169663f9c8b245e5b01b5422742` dated August 6, 2026. Original Git
history is retained, and this derivative is not endorsed by AppleDeath318.

Dedicated-server compatibility research was informed by **HexTool V0.2.3 -
Server Update** (**Hex Tool / HexTool**), including its prior custom-map
distance-cache and server-module-filtering work. HexTool is separate, is not
required, and its source or binaries are not included or redistributed here.
The supplied package did not identify a creator, canonical URL, or license, so
this repository records the exact verifiable package name without inventing an
author claim.

See [`NOTICE.md`](NOTICE.md) for the complete notice.

## License

This project is free software distributed under the **GNU General Public
License version 3 only** (`GPL-3.0-only`). See [`LICENSE`](LICENSE).

If you distribute a build or modified covered version, provide the complete
corresponding source under the same license and preserve the required notices.

## Disclaimer

This is an independent community utility. It is not an official TaleWorlds,
Bannerlord Coop, EOE, HexTool, or AppleDeath318 product and is not endorsed by
those projects or authors. All third-party names and assets remain the property
of their respective owners.
