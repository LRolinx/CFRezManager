# LTB structure audit

Run from the repository with the .NET 8 SDK on Windows:

```powershell
dotnet run --project tools/LtbStructureAudit -c Release -- C:\Extracted C:\Reports\LtbStructure
```

The input is an extracted resource directory. The tool reads loose `.ltb` files recursively. If the directory contains a six-column `resource-manifest.tsv` (`archive`, `resource_path`, `size`, `archive_md5`, `sha256`, `extracted_path`), it uses those original source labels and checks each file's stored SHA-256.

The validated layout is native D3D LTB header version 9, model version 25, including an optional LZMA wrapper. Each file is bounded to 128 MiB. Other layouts produce an error row.

## Output

- `models.tsv`: source identity, file hashes, allocation and parsed counts, hierarchy/bind-matrix hashes, parse status and error. Weight-set, child-model and animation-binding counts are appended after the original columns.
- `nodes.tsv`: node index, parent index, name, flags and the serialized global bind matrix.
- `animations.tsv`: per-file animation names, compression types, interpolation times, keyframe counts and final keyframe times. Names repeated in different physical model copies remain separate records.
- `sockets.tsv`: embedded socket names, target node indices and serialized rotation/position/scale values.
- `weight-sets.tsv`: named animation blend masks, node counts and serialized float32 weights in node-index order. These are not vertex skin weights.
- `child-models.tsv`: child index, implicit-self flag, raw declared path and serialized animation-binding count. Index 0 is the containing model and has no path string in the file.
- `animation-bindings.tsv`: child index, binding index, animation name, dimensions and translation. Self bindings also report case-insensitive name-match counts and same-index name equality; external children are not compared with the containing model's animations. These metadata values are separate from bone keyframes.
- `inspection.tsv`: whether animation channels were requested for this run.
- `channels.tsv` and `keyframes.tsv`: channel payloads and event records when `--channels` is present. Normal runs write empty tables and mark channel inspection as unrequested, clearing any records from a previous run in the same output directory.

Matrix, socket, weight and binding vector values are little-endian float32 bytes encoded as hexadecimal. Hierarchy hashes cover node index, parent, name and flags sorted by index. Bind-matrix hashes compare serialized values, so different hashes do not by themselves prove incompatible poses. Duplicate or reordered binding names remain visible in the report; structural completeness does not establish a unique animation association.

`structure-complete` requires the declared node, piece, LOD, weight-set, child-model, animation and socket counts to agree, bounded animation channels, and complete file consumption with at most three zero padding bytes. All detailed tables are emitted only for complete files. Exit code 1 reports rejected files; exit code 2 reports invalid arguments.

The tool inventories animation data without evaluating poses. Matching node hierarchies is evidence for an association; animation playback and retargeting still require validation. OBJ export remains geometry and texture export.

Vertex skin data has a separate [capture check](../NativeSkinCheck/README.md). The PView/arm snapshot verifies 160 LOD0 meshes, 141,464 vertices and 2,395 draw bindings against independent reads. Capture ends after the node hierarchy and therefore does not replace this audit's complete-file checks. Bone/weight decoding alone does not validate rendered deformation.

Pass this tool's completed `--channels` report to the skin tool's `--poses` option for sampled CPU evaluation. It verifies source hashes, indexed nodes and channel payloads before sampling the first/last keys and a selected interval midpoint. The [241-pose evidence](../../docs/evidence/native-pose-20261105-validation.tsv) covers native model-space transforms and vertex positions; blending, external-child selection and game rendering remain unverified.

The [body inventory](../../docs/evidence/character-bodies-20261105-structure-models.tsv) adds 947 complete physical files with 27,776 nodes, 7,502 sockets, 1,894 blend masks and 946 external child declarations. None contains an embedded animation. An [independent reader](../../docs/evidence/character-bodies-20261105-structure-validation.tsv) reproduced every retained value and consumed each file completely. Child paths remain declarations; physical motion selection and playback compatibility are unverified.

The [full CharacterItem inventory](../../docs/evidence/characteritem-all-20261105-structure-models.tsv) covers 2,666 physical LTBs, including earlier accessory samples. It retains 8,600 nodes, 2,666 embedded animations, 109 blend masks, 2,666 self bindings and one socket. The [independent comparison](../../docs/evidence/characteritem-all-20261105-structure-validation.tsv) reproduces all records and complete file consumption. Raw attachment enums remain separate from external bone bindings, and the inventory does not evaluate poses.

The [Nano inventory](../../docs/evidence/nano-family-20261105-structure-models.tsv) records 103 complete native models with 6,611 nodes, 12,178 embedded animations, 333 sockets, 198 blend masks and 49 external child declarations. The [independent reader](../../docs/evidence/nano-family-20261105-structure-validation.tsv) reproduces every retained value and complete file consumption. Four additional LTB-extension resources are declared render styles with another header and correctly produce no model details. The [1,779 motion pairs](../../docs/evidence/nano-family-20261105-motion-compatibility.tsv) retain hierarchy, bind-matrix and relative child-path comparisons without selecting a physical version or proving playback.

## Channel inspection

```powershell
dotnet run --project tools/LtbStructureAudit -c Release -- C:\Extracted C:\Reports\LtbStructure --channels
```

`channels.tsv` records each node's position/rotation payload offset in the decoded LTB, byte count, SHA-256, sample count, encoding and first/last decoded values as float32 hexadecimal. It follows serialized hierarchy traversal, retaining each node's actual index. Uncompressed channels store all positions followed by all quaternions. The four supported encodings are uncompressed, full-float relevant channels, signed int16 positions/rotations, and full-float positions with int16 rotations. Int16 positions divide by 16; quaternion components divide by 32767, without clamping or normalization.

Zero compressed samples retain a default zero position or identity quaternion. One sample is constant; larger channels must contain one sample per keyframe. Partial channels without a validated frame binding are rejected. Float samples must be finite. Vertex-animation payloads remain separate records with their frame-list bytes and hashes.

`keyframes.tsv` retains each key index, time in milliseconds and raw event bytes in `event_cp949_hex`. The text column is a CP949 interpretation with display control characters replaced by spaces; the hexadecimal column preserves the original bytes. All detailed rows require a complete file parse. A structure-only run bounds the payloads but does not perform these sample checks.

The [PView snapshot](../../docs/evidence/pview-motion-20261105-channel-validation.tsv) independently verifies 13,088 channels and 2,318 keys from 37 files. The two `PV-HuluXiongdi.ltb` copies have identical transform channels but [different sound-event timing](../../docs/evidence/pview-motion-20261105-event-differences.tsv). Sixteen variants explicitly reference their base PView; their [child candidates](../../docs/evidence/pview-motion-20261105-child-candidates.tsv) keep hierarchy and bind-matrix comparisons separate.

CharacterType arm selection has a separate [context check](../CharacterArmCheck/README.md): side-qualified row, identity and RecordId selectors must retain their own skins when several characters or sides share an arm model. Structure completeness alone does not verify that skin selection.

The [arm inventory](../../docs/evidence/character-arms-20261105-structure-models.tsv) covers 704 complete physical files, with 32,384 nodes, 704 embedded animations and 1,913 sockets. Each has a 46-node hierarchy, one blend mask, one implicit self child and one animation binding. No external child is serialized in these files. The [raw CFT fields](../../docs/evidence/character-arms-20261105-raw-fields.tsv) preserve row-level body-child references separately; they do not prove arm animation playback.

For CFT source fields referenced by the mapping records, v1.2.7 opens the complete decoded table as a grid with row numbers, numeric sorting and a frozen `RecordId` column. Export XLSX from that window or with `--export-cft-xlsx --input <file.cft> --output <file.xlsx>`. Preview sorting does not change the exported source order. The workbook's `RecordId` column is the serialized identifier, not a zero-based row index.

The FireAim snapshot records 17 complete physical LTB files. Checks using a truncated file, invalid node count, unsupported version and unexpected trailing bytes reject all four altered files and emit detailed rows only for the valid control.

The CharacterItem base-field snapshot records 272 complete physical LTB files, including related models. Each contains two nodes, one embedded animation and no embedded socket. The [mapping register guide](../../docs/asset-mapping-register.md) links their source hashes, 544 node records, 272 animation records, texture-copy checks and export results.

The CharacterItem side snapshot adds a separate inventory of 91 physical files, which can overlap the base snapshot. It records 182 nodes, 91 embedded animations and one socket: `Effect` on node 1 of `Models/WAVE/ETC/IFF_Shoulder.LTB`. The [node](../../docs/evidence/characteritem-side-20261105-structure-nodes.tsv), [animation](../../docs/evidence/characteritem-side-20261105-structure-animations.tsv) and [socket](../../docs/evidence/characteritem-side-20261105-structure-sockets.tsv) tables retain every parsed entry. The [raw CFT fields](../../docs/evidence/characteritem-side-20261105-raw-fields.tsv) cover 140 rows; target-type enums do not establish external socket bindings. v1.2.8 also keeps GR/BL mapping contexts separate when their model paths are identical.

v1.2.9 preserves these structure records while rejecting conflicting texture copies during export. The [texture-copy inventory](../../docs/evidence/texture-copy-20261105-variants.tsv) records the six affected physical DTX files; the [copy-check tool](../TextureCopyCheck/README.md) tests pixel equivalence and fallback behavior separately from model structure parsing.

The [combined association snapshot](../../docs/evidence/ltb-associations-20261105-models.tsv) deduplicates the three inventories into 373 physical files. It preserves 376 child entries, 8,867 animation bindings and 17 blend-weight sets. The three FireAim body files explicitly name `M-motion_blue.ltb` as an external child with zero serialized binding overrides. Their arm files have no external child. All prior structure rows and all newly recorded binary values passed independent comparison; physical motion-version selection and playback remain unverified.

## Structure checks

```powershell
.\tools\LtbStructureAudit\Check.ps1
```

The script builds the tool into a unique directory under `artifacts/ltb-structure-check/`, creates 26 synthetic files without game resources and runs 128 assertions. The original 40 checks cover child declarations and bindings. Additional cases cover all four channel encodings, zero/default channels, vertex payloads, raw events, swapped node indices, exact payload hashes, partial channels, non-finite samples and stale-output clearing. Invalid fixtures produce summary errors without detailed rows. Successful checks exit with code 0 even though the rejection fixtures intentionally return code 1 from the audit tool. Results default to `artifacts/ltb-structure-check/results.tsv`; `-Output <report.tsv>` changes that path. `-ToolDll <LtbStructureAudit.dll>` uses an existing build. The release workflow runs the checks before packaging.

## Motion comparison

`CompareMotion.ps1` compares explicit pairs using complete `models.tsv` and `nodes.tsv` records. The pair input needs `parent_source` and `motion_source` columns; it may also be the output file, because all input is read before writing. The script rejects missing model/node records and duplicate node indices.

```powershell
.\tools\LtbStructureAudit\CompareMotion.ps1 `
  -Models docs\evidence\fireaim-marker-20261105-structure-models.tsv `
  -Nodes docs\evidence\fireaim-marker-20261105-skeleton-nodes.tsv `
  -Pairs docs\evidence\fireaim-marker-20261105-motion-compatibility.tsv `
  -Output C:\Reports\motion-compatibility.tsv
```

The output includes name overlap, non-root matches, duplicate names and hierarchy/bind-matrix equality. `has_shared_node_names` means only that at least one node name overlaps. It does not establish a valid animation binding. The FireAim arms share `Scene Root` alone; none of their non-root names match the motion source.
