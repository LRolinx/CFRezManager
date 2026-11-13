# Texture copy checks

The [CFT catalog checks](../../docs/asset-mapping-register.md#cft-catalog-and-node-templates) add six cases around deferred variant validation in the context suite. An eligible inferred binding still verifies decoded copies; untyped, absent and directly bound variants skip unused pixel validation. Physical copies are grouped by exact resource path, and repeat checks reuse the byte/pixel consistency result. The mapper scopes CFT/LTC work to the requested model and reuses agreed slot layouts for physical groups. The 49 copy checks pass. A focused one-model export now succeeds, but full-root indexing and export remain unverified.

The [other-CFT comparison](../../docs/asset-mapping-register.md#other-model-tables) decodes 174 physical DTXs. Two IronBeast2 NG paths each have different pixels in their physical copies. Restored row bindings keep those conflicts unresolved and stop 16 unrelated old-skin fallbacks; every stopped image matches none of the declared copies. All 49 copy checks pass, and 253 written PNG witnesses remain byte-identical.

The [UGC snapshot](../../docs/asset-mapping-register.md#ugc-assets) decodes 3,003 physical DTXs and retains 17 logical paths with conflicting pixels. Every written requested or grouped export matches a unique source pixel result; zero-alpha removal is recorded. Across those exports, 32,723 PNG witnesses remain byte-identical. The 49 synthetic copy checks pass; recorded object texture candidates do not select a physical version or a scene texture.

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

The SwatElite Renewal export uses the hand BL and arm GR paths declared by CharacterType row 18/A0017. The focused resource manifest finds one physical DTX copy at each logical path; their hashes and exported slot results are recorded [here](../../docs/evidence/swat-elite-renewal-20261114-resources.tsv) and [here](../../docs/evidence/swat-elite-renewal-20261114-export-slots.tsv). This snapshot does not add a separate run of the synthetic copy-check suite.

The WildWest SP arm has no SP-specific hand or arm DTX in the scanned resources and no CFT texture field selecting one. Eight BLGR/Santa paths are retained as unbound candidates; their presence alone does not map them to SP. The [candidate list](../../docs/evidence/wildwest-sp-20261114-texture-candidates.tsv) and [slot report](../../docs/evidence/wildwest-sp-20261114-export-slots.tsv) preserve the missing result.

Animation equivalence needs separate evidence. Two recorded PView copies have identical transform channels but [different sound-event timing](../../docs/evidence/pview-motion-20261105-event-differences.tsv); the [channel inspection](../LtbStructureAudit/README.md#channel-inspection) retains their exact payload and event bytes.

The [native skin checks](../NativeSkinCheck/README.md) also run before packaging. They preserve default geometry and texture slots while checking optional bone/weight capture. Skin data for 160 meshes has been independently reproduced; this does not select a physical texture copy or establish rendered animation.

The same tool's pose checks now cover root animation shifts, parent transforms and CPU skinning. The [pose evidence](../../docs/evidence/native-pose-20261105-sources.tsv) keeps the two PView copies separate and retains the unselected external children. These calculations do not resolve texture-copy conflicts or animation event differences.

The [body texture inventory](../../docs/evidence/character-bodies-20261105-texture-copies.tsv) records 1,503 decoded physical DTXs and [48 conflicting logical paths](../../docs/evidence/character-bodies-20261105-texture-conflicts.tsv). The [context checks](../CharacterArmCheck/README.md) separately cover shared body models, declared texture slots and missing skins. Selecting the correct CFT side does not resolve a conflicting physical texture version.

The [full CharacterItem inventory](../../docs/evidence/characteritem-all-20261105-texture-copies.tsv) adds 2,575 decoded DTX copies and [24 conflicting logical paths](../../docs/evidence/characteritem-all-20261105-texture-conflicts.tsv), which can overlap earlier inventories. All confirmed written pixels match the selected field. That context snapshot used 237 checks, including DTX fixtures whose filename suffix disagrees with the declaring side.

The Clan Backpack texture `ModelTextures/SocketItem/BACK/B_CLANBAG.DTX` has one decoded physical copy in this snapshot. Its CFT BL and GR fields share the path; separate `Box02` slot-0 exports match the same recorded DTX and PNG hashes. `Plane01` slots 1–4 remain unresolved because their CFT fields are empty. The [focused export record](../../docs/evidence/characteritem-socket-export-20261114.tsv) includes the source and PNG hashes.

The [Nano DTX inventory](../../docs/evidence/nano-family-20261105-texture-copies.tsv) verifies all 218 physical textures. Four logical paths have conflicting pixels; the [slot results](../../docs/evidence/nano-family-20261105-export-slots.tsv) keep 130 affected physical witnesses unresolved. That context snapshot used 357 cases, including 120 Nano field/selector/export checks; this tool retains its separate 49 texture-copy cases.

The [AI texture inventory](../../docs/evidence/ai-family-20261105-texture-copies.tsv) verifies 676 physical DTXs, including typed references under Models/. Sixteen logical paths have conflicting pixels; the [slot results](../../docs/evidence/ai-family-20261105-export-slots.tsv) keep 158 affected physical witnesses unresolved. Every written image matches its selected DTX pixels. That context snapshot used 433 cases; the texture-copy suite remains 49 cases.

The [doll/accessory inventory](../../docs/evidence/view-items-20261105-texture-copies.tsv) verifies 540 physical DTXs and retains four conflicting logical paths. The [1,064 stage witnesses](../../docs/evidence/view-items-20261105-export-slots.tsv) preserve exact source/output pixels, including 210 new outputs and two corrected female ring skins. Directory/Baihu aliases remain unverified as direct row declarations. The context suite now has 519 cases, including gender-prefix expansion and invalid male-copy isolation; this tool retains 49 cases.


The [CharacterType arm binding table](../../docs/evidence/character-arms-20261114-logical-bindings.tsv) records 36 directly referenced DTX paths across 80 logical slots. Every path has multiple physical copies with different decoded pixel hashes, so this snapshot leaves the active version unresolved.

The separate [female GR arm exports](../../docs/evidence/character-arms-20261114-sidefield-exports.tsv) use `ModelTextures/PlayerView/FVIEW_ARM_SOV_BL.DTX`, which has one physical copy in the inspected resources. Twelve row selectors export `Object05` slot 1 with matching PNG hashes; the `_BL` filename suffix does not change the `_GRPVArmTextureFile` declaration's side.

The nine KingsMission Silver target contexts use `AI_TARGET_SILVER.dtx`, whose single physical copy produced the verified slot-0 export under AICharacter record 132. Forty-seven repeated texture columns reuse row-level slot exports without introducing new DTX copies. See the [post-END records](../../docs/evidence/kingsmission-post-end-20261114.tsv) and [duplicate-field evidence](../../docs/evidence/kingsmission-duplicate-texture-columns-20261114.tsv); surrounding trailer bytes remain partly uninterpreted.
