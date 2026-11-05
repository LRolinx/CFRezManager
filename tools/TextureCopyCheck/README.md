# Texture copy checks

Run from the repository on Windows with the .NET 8 SDK:

```powershell
dotnet run --project tools/TextureCopyCheck -c Release
```

The default run creates small PNG and REZ fixtures under `artifacts/texture-copy-check/` and checks 49 cases. Results are written to `artifacts/texture-copy-check/results.tsv`; `--output <report.tsv>` selects another report path. The release workflow runs this command before building its package.

Checks cover identical bytes, identical pixels with different DPI metadata, PNG metadata whose encoded size matches raw-TGA dimensions, PNG payloads referenced as TGA, recognized but invalid PNG data, RGB and alpha changes in the first and last rows, changed dimensions, invalid copies, reversed copy order, different logical paths with the same basename, stale or misleading index MD5 values, explicit physical paths and local/archive fallback behavior. Existing conflicting resources must not fall through to another extension or a nearby copy; a path absent from the global index can still use the local resolver.

An optional 2026-11-05 CharacterItem resource snapshot adds 12 checks for the CFPL GR headset and The_Fates_Casual GR hair/hat copies:

```powershell
dotnet run --project tools/TextureCopyCheck -c Release -- --resources C:\Snapshots\CharacterItem --output C:\Reports\texture-copies.tsv
```

The snapshot needs its six-column `resource-manifest.tsv` and extracted files, with two physical copies of each of those three logical DTX paths. The checks reject unqualified conflicting paths in either order and retain access to each explicit physical path. Raw game resources are not included in the repository.

Model associations have a separate [structure check](../LtbStructureAudit/README.md#structure-checks). Its 40 assertions cover child declarations, animation bindings and node blend masks, and also run before release packaging. Passing texture-copy checks alone does not establish those relationships or animation playback.

The [character arm checks](../CharacterArmCheck/README.md) cover side-qualified CFT selectors and preserve both declarations when GR and BL share a model path. These checks run before release packaging; a matching logical texture still needs valid physical copies.

The [arm texture inventory](../../docs/evidence/character-arms-20261105-texture-copies.tsv) records 1,464 decoded DTX copies and [36 conflicting logical paths](../../docs/evidence/character-arms-20261105-texture-conflicts.tsv). These conflicts remain unresolved regardless of which character context selects the path.

Animation equivalence needs separate evidence. Two recorded PView copies have identical transform channels but [different sound-event timing](../../docs/evidence/pview-motion-20261105-event-differences.tsv); the [channel inspection](../LtbStructureAudit/README.md#channel-inspection) retains their exact payload and event bytes.

The [native skin checks](../NativeSkinCheck/README.md) also run before packaging. They preserve default geometry and texture slots while checking optional bone/weight capture. Skin data for 160 meshes has been independently reproduced; this does not select a physical texture copy or establish rendered animation.
