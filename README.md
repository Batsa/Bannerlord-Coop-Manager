# Bannerlord Coop Manager

Bannerlord Coop Manager is a Windows desktop manager for a Bannerlord Coop
dedicated server. The current executable and user interface retain the upstream
name **BCS Tool**.

This repository publishes a **preview Windows build**. It extends BCS Tool
with dedicated-server module management, reversible Coop bridge installation,
client bridge packaging, client-save import, persistent server
logging, and a version-scoped compatibility path for **Empires of Europe 1700
(EOE)**.

> Current application version: `0.3.0-beta.2`
>
> Current generated bridge runtime: `0.6.60`
>
> Upstream base: [`AppleDeath318/BCSTool@f7bc05c`](https://github.com/AppleDeath318/BCSTool/commit/f7bc05c672dad169663f9c8b245e5b01b5422742)

## Status at a glance

The EOE work is promising but is **not yet a stable or general compatibility
release**.

| Area | Current status |
|---|---|
| GUI server lifecycle, configuration, backups, and console | Implemented and regression-tested |
| Dedicated-server module profile and enforced UDP port `4200` | Implemented and regression-tested |
| Client campaign import into the server | Implemented and regression-tested |
| Version-scoped server/client bridge generation | Implemented and regression-tested |
| EOE `1.4.7.1` dedicated-server bridge installation | Version/file/signature validated implementation |
| EOE server startup and client join | Demonstrated in the prior hand test |
| EOE 1696x1696 map/weather correction | Hand-tested; observed weather/MapEvent index failures stopped |
| Ordinary Coop battles | Multiple battles completed in the prior hand test |
| Bridge `0.6.60` overlays, lifecycle fixes, and version-scoped compatibility | Built and regression-tested; live hand retest pending |
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
| Dedicated-server game runtime | `1.4.7` |
| Generated bridge | `0.6.60` |

These are supported compatibility versions, not floating minimum versions. The
bridge installation validates versions, required files, paths, assembly identities,
and required method signatures. It does not reject compatible files merely
because their bytes or hashes differ. A game or Workshop update requires a new
**Install/Update Bridge → reinstall client ZIP** cycle.

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
   `Bannerlord-Coop-Manager-v0.3.0-beta.2-win-x64.zip`. Do not download the
   automatically generated **Source code** archives.
3. Right-click the downloaded ZIP and select **Extract All**. Extract it to a
   normal writable folder, such as `Documents\Bannerlord Coop Manager`.
4. Open the extracted folder and run `BCS Tool.exe`.

Keep every extracted file together. In particular,
`BCSTool.RuntimeBootstrap.dll` must remain beside `BCS Tool.exe`; it is used
when the manager starts a modded dedicated-server profile.

The preview executable is self-contained for Windows x64 but is not digitally
signed. Windows may therefore show an **Unknown publisher** or SmartScreen
warning. Verify the ZIP against its adjacent `.sha256` release asset before
running it. `START-HERE.txt` inside the ZIP repeats these instructions.

The ZIP contains only Bannerlord Coop Manager. Bannerlord, Bannerlord Coop,
EOE, and the dedicated-server package must already be installed separately.

## Initial server setup

1. Install or update Bannerlord, Bannerlord Coop, and EOE to the exact versions
   above.
2. Exit Bannerlord and stop the dedicated server completely.
3. Launch `BCS Tool.exe`.
4. Confirm that BCS Tool detected `BannerlordCoopServer.exe`. Use **Browse** if
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

BCS Tool preserves the existing JSON-with-comments layout where supported and
creates a sibling `.bak` before replacing a configuration file.

## Install or update the EOE bridge

Bridge installation is deliberately validated, reversible, and narrower
than ordinary mod conversion.

1. Stop the server completely.
2. Open **Server Mods**.
3. If EOE is not present in the dedicated-server module directory, drag its
   module folder—the folder containing `SubModule.xml`—onto the module list.
4. Select EOE and click **Analyze for Coop** if you want the read-only report.
5. Click **Install/Update Bridge**.
6. Confirm the single backed-up install/update operation. It enables EOE and
   writes the required load order as part of the same action.
7. Record the generated bridge ID and client ZIP path shown by the tool.

There is no separate Prepare step. The same bridge operation owns EOE DLL
projections under `Europe1700\bin\Win64_Shipping_Server`, the
`conf_clans_resource_adder.xml` projection beside `ClansResourceAdder.dll`,
headless XML overlays, the server load-order profile, the generated bridge
module, and its matching client ZIP. Start runs the same recipe as a preflight
and repairs missing bridge-owned projections before launching Bannerlord.

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

### What the EOE bridge installation currently addresses

- The dedicated server's incorrect 848x848 terrain size for EOE's 1696x1696
  world map
- Delayed client handler discovery so the bridge does not hard-reference
  Coop's `Common.dll` before Coop loads
- Headless action/action-type and malformed trebuchet XML adaptation
- Exact workshop recipe repair for EOE's populated ranged-weapon tier
- Invalid Bearskin Cape references and legacy civilian equipment attributes
- Direct XSLT-load redirection through Bannerlord's required `ApplyXslt` path
- Narrow client MapEvent removal authority and troop-upgrade after-load repair
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

If EOE, Coop, or the game updates, rerun **Install/Update Bridge** and distribute the newly
generated ZIP. BCS Tool does not download or silently update Workshop mods.
The ZIP contains the bridge runtimes, manifest, configuration, GPL notice, and
attribution. Server-only transformed EOE XML/XSLT overlays stay on the server
and are not redistributed in the client ZIP.

## Seed the server from a client campaign

The server needs an existing EOE campaign. Character creation remains a normal
Bannerlord client task:

1. Start a normal Bannerlord Sandbox game with the exact EOE build.
2. Create the character and campaign.
3. Save manually.
4. Exit Bannerlord completely so the save is stable.
5. In BCS Tool, open **Server Configuration** and click **Refresh Saves**.
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

BCS-managed module-profile launches invoke the engine with:

```text
/dedicatedcustomserver 4200 EU 0
```

Port `4200` is enforced by the managed launch plan; it is not read from
`server-config.json`. If no `bcs-server-modules.json` exists, BCS Tool uses the
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

BCS Tool's own lifecycle log is stored under:

```text
%LOCALAPPDATA%\BCSServerTool\Logs\BCSTool-yyyy-MM-dd.log
```

Before sharing logs, remove server passwords, public addresses, Steam IDs, and
player-identifying information.

## Backups, rollback, and safety

- Stop Bannerlord and the server before campaign import, bridge installation, or
  revert.
- Bridge installation re-checks inputs before writing and aborts if anything changed
  while its installation plan is being applied.
- **Revert Bridge Install** uses the recorded backup manifest; do not manually
  edit that manifest.
- Do not manually copy Harmony, Coop, game, or mod DLLs into the dedicated
  server's engine root.
- Save backups treat `<name>.sav` and `<name>.json` as one pair.
- One to five rotating generations are kept under
  `DedicatedServer\Game Saves\BCS Backups`.
- Manual restore is available only while the managed server is stopped.
- Automatic corruption detection and automatic rollback are not implemented.

## Known limitations

- Client movement on the world map was still teleporty, sticky, or laggy in
  the last hand test. New diagnostics can determine whether party
  disorganization or missing TroopRoster registration contributes, but no
  speculative movement rewrite has been added.
- The `0.6.60` workshop/Bearskin/civilian overlays and client lifecycle
  fixes have deterministic build and regression coverage but still need a new
  full hand test.
- Final-build save/reconnect, late join, long-duration synchronization, and
  repeated battle acceptance are not complete.
- Large EOE campaign saves around 100 MiB previously caused multi-second
  synchronous save stalls.
- Sixteen EOE firearm `Weapon` schema warnings remain intentionally: the second
  nodes carry functional alternate melee modes and removing them would break
  weapons.
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

The regression runner currently contains 50 named checks and finishes with:

```text
All BCS Tool regression checks passed.
```

### Publish the application

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\BCSTool\Publish-SingleExe.ps1
```

Publish output:

```text
BCSTool\publish\BCS Tool.exe
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
