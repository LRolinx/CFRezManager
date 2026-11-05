# Native LTB skin checks

Run on Windows with the .NET 8 SDK:

```powershell
dotnet run --project tools/NativeSkinCheck -c Release
```

The tool runs 90 checks without game files and writes `artifacts/native-skin-check/checks.tsv`. The original 63 cover direct draws, rigid effectors, global/remapped matrix palettes, slot 255 and node indices above 255, overlapping vertex ranges, per-corner overrides, unused vertices, multiple streams, raw/implicit weights and invalid ranges or skeletons. Another 27 cover native pose transforms and CPU skinning. Ordinary geometry, UV sets, texture slots and render-style indices must remain unchanged. The release workflow runs these checks before packaging.

## Inspect a resource snapshot

```powershell
dotnet run --project tools/NativeSkinCheck -c Release -- `
  --resources C:\Snapshots\PView `
  --output C:\Reports\NativeSkin `
  --vertices
```

Input requires extracted files and a six-column `resource-manifest.tsv`: `archive`, `resource_path`, `size`, `archive_md5`, `sha256`, `extracted_path`. Each file's stored SHA-256 must match. Native D3D LTB header version 9/model version 25 and optional LZMA wrapping are supported within a 128 MiB bound. The current decoder captures LOD0 geometry and its complete indexed node hierarchy. Animation/footer validation remains the [structure audit](../LtbStructureAudit/README.md).

Outputs:

- `meshes.tsv`: method, counts, errors, canonical geometry and skin hashes, stored-weight stream/decoded offsets/stride and palette metadata.
- `draws.tsv`: triangle-index start/count, declared vertex bounds and direct bone sets or rigid effector.
- `nodes.tsv`: indexed hierarchy and serialized global bind matrices as float32 hexadecimal.
- `bone-usage.tsv`: weighted vertex/corner counts and float32 weight ranges per mesh/node. A vertex shared across draws can contribute to several nodes.
- `vertices.tsv`: four little-endian int32 node indices and four float32 weights per vertex when `--vertices` is present.
- `corner-overrides.tsv`: alternate bindings for individual triangle corners when `--vertices` is present.
- `inspection.tsv`: whether vertex values and native poses were requested. A run without `--vertices` clears both skin-detail tables to their headers.

Skin hashes cover vertices in source order, each with four int32 indices then four float32 weights. Sorted corner overrides follow as an int32 corner index and four interleaved int32-node/float32-weight pairs. Unused slots have node `-1` and weight zero. Geometry hashes compare capture with the ordinary decoder and include positions, indices, all UV sets, texture slots and render style.

## Binding rules and limits

`LithTechModelDecoder.TryDecodeWithSkinning` is an optional internal entry point. It attaches `NativeSkeleton` and per-mesh `NativeSkinning`; ordinary preview and OBJ decoding do not allocate these arrays. Invalid skin data reports a per-mesh error while retaining static geometry. Invalid hierarchy data rejects capture. Reading through the hierarchy does not establish that the remaining file is valid.

Direct bone sets apply to the actual triangle indices between successive cumulative ends. Vertex bounds alone cannot assign a bone. Shared vertices with different draw bindings retain corner overrides for a future exporter to split. Each active draw uses its stored leading weights and an implicit final weight of `1 - sum(previous weights)`; finite values are retained without clamping or normalization. Matrix-palette bytes address slots directly, without adding the minimum. Remapped global node IDs can exceed 255, and palette slot 255 is valid when in range.

The [recorded 37-file check](../../docs/evidence/native-skin-20261105-validation.tsv) independently reproduces 160 meshes, 141,464 vertices, 150,648 triangles, 2,395 draw groups and all 2,146 existing node records. It covers 88 direct and 72 rigid meshes, with no corner overrides or weights outside the unit interval. Matrix palettes currently have synthetic coverage only. Physical motion-version selection, rendered poses and skeletal animation export remain unverified. OBJ continues to export geometry and textures.

## Sample native poses

First produce a complete channel report, then inspect the matching snapshot:

```powershell
dotnet run --project tools/LtbStructureAudit -c Release -- `
  C:\Snapshots\PView C:\Reports\Channels --channels
dotnet run --project tools/NativeSkinCheck -c Release -- `
  --resources C:\Snapshots\PView --output C:\Reports\NativePose `
  --poses C:\Reports\Channels
```

The source must match the complete report's stored/decoded hashes, skeleton values and channel payload hashes. Every node needs supported position/rotation channels; partial channels, vertex animation and mismatched self binding indices/names reject pose inspection. External children are recorded as unselected. Each embedded animation contributes its first key, last key and the midpoint of its longest positive adjacent-key interval; a single-key animation contributes one pose.

Additional outputs:

- `pose-sources.tsv`: physical sources, animation/sample counts and the unselected-child status.
- `poses.tsv`: sampled frame indices/fraction/time, root translation and hashes of indexed local/global/skin matrices.
- `pose-nodes.tsv`: each sampled node's matrices in native column-vector order, as little-endian float32 hexadecimal.
- `pose-meshes.tsv`: vertex/override counts, binary offsets, exact position hashes and float32 bounds.
- `pose-vertices.bin`: consecutive XYZ float32 points, first in source vertex order and then in sorted corner-override order.

A resource audit clears prior pose tables and binary data before inspection. Without `--poses` they remain empty and the inspection marker says false. Rejected input cannot retain a previous completed pose summary. [Nine recorded checks](../../docs/evidence/native-pose-20261105-input-guards.tsv) exercise these guards.

`LithTechPoseEvaluator.Evaluate` accepts indexed endpoint samples, an interpolation fraction and the selected self binding's root translation. Returned matrices use System.Numerics row vectors, transposed from the serialized LTB convention. It composes local with parent transforms and applies the loader's rigid bind inverse before the animated global transform. Rotation-only nodes use the parent-relative bind offset. Quaternion interpolation follows the shortest arc; matrix conversion accounts for quaternion magnitude. `SkinVertex` applies finite weights without normalization and rejects unbound vertices. Callers must use `GetCornerSkin` for shared vertices with alternate draw palettes.

The [independent pose check](../../docs/evidence/native-pose-20261105-validation.tsv) covers 241 samples, 19,340 node poses and 1,100,023 vertex positions from 21 animated files. Sixteen other files have no embedded animation. The separate column-vector calculation differs by at most 0.000017543 in matrix components and 0.000008037 in vertex components. Rotation-only nodes are covered synthetically because this snapshot has no such flags. These samples do not validate continuous playback, blending, node controllers, external-child retargeting or export coordinate conversion. GUI animation playback and animated export are not integrated.

The separate [947-file body structure inventory](../../docs/evidence/character-bodies-20261105-structure-models.tsv) records skeletons, sockets and external motion declarations. It has no embedded animations and has not undergone this tool's vertex-skin/pose comparison. The [character context checks](../CharacterArmCheck/README.md) validate body texture selection and static OBJ/PNG output separately.

The [2,666-file CharacterItem inventory](../../docs/evidence/characteritem-all-20261105-structure-models.tsv) records embedded animation metadata and indexed bones, without applying this tool's pose/skin validation. The context suite now has 357 checks for arms, bodies, socket items and Nano character skins. Successful texture exports and recorded animation names do not establish attachment transforms or playback compatibility.

The [Nano structure snapshot](../../docs/evidence/nano-family-20261105-structure-validation.tsv) independently verifies 103 complete native files, 6,611 nodes and 12,178 embedded animations. Its [motion comparisons](../../docs/evidence/nano-family-20261105-motion-compatibility.tsv) retain 47 logical model/child relationships across 1,779 physical row pairs. These records do not apply this tool's vertex-skin or sampled-pose validation to those models.
