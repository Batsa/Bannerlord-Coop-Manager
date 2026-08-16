# Battle-scene catalog contract provenance

`europe-1700-1.4.7.1-sandboxcore-1.4.8.bcs` is a reviewed release input. The
launcher embeds and redistributes this manifest; neither the launcher nor the
runtime generates trusted hashes from an operator installation.

Pinned source snapshot:

- Steam app/content: `261550/3231544373`
- Steam Workshop manifest: `3706989296849270590`
- Europe1700 module version: `v1.4.7.1`
- SandBoxCore module version: `v1.4.8`
- Bannerlord Steam build ID: `24573425`
- Bannerlord depot manifests from the completed Steam installation:
  `261551/2130492496439118726`, `261552/652956066029874796`, and
  `2240111/6194812840481947105`
- Europe1700 `ModuleData/sp_battle_scenes.xml` SHA-256:
  `2A3603606BAB8A777EF761040AD146ADDD7CF1061163E2BB6F7F9EF04E99BD03`

The Workshop manifest was recorded from Steam's
`appworkshop_261550.acf`; the game build and depot manifests were recorded
from `appmanifest_261550.acf` after Steam reported the installation current.
The contract was then generated from that read-only Steam-managed EOE and
SandBoxCore snapshot and verified back against every listed source file.

Deterministic generation procedure:

1. Read `/SPBattleScenes/Scene` elements in XML document order. Emit a
   zero-based `CANDIDATE` record using the exact `id`, `terrain`,
   `forest_density`, and `map_indices` attribute strings. Encode text as
   canonical Base64 of strict UTF-8. Do not deduplicate rows.
2. For every distinct scene ID, resolve `Europe1700/SceneObj/<id>` first and
   `SandBoxCore/SceneObj/<id>` second. Missing or ambiguous ownership fails the
   release procedure.
3. Recursively enumerate regular, unlinked files in those directories. Exclude
   the case-insensitive editor/Windows metadata names `desktop.ini` and
   `references.txt`; they are not runtime scene inputs. Emit module ID,
   module-relative forward-slash path, byte length, and uppercase SHA-256.
4. Sort `ASSET` records by module ID and then relative path using ordinal,
   case-sensitive comparison. Write UTF-8 without BOM, LF line endings, and a
   final LF.
5. Verify exactly 196 `CANDIDATE` records, 559 `ASSET` records, and final
   contract SHA-256
   `73A8FE6A386CAC331CA25182AE3268E7A52E7DE8BA21A954F1FC0B5A3FB61D54`.

Changing any pinned input requires a newly reviewed, versioned contract and a
new recipe mapping. It must not overwrite this historical artifact.
