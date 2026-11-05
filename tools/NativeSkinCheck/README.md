# Native LTB skin checks

Run on Windows with the .NET 8 SDK:

```powershell
dotnet run --project tools/NativeSkinCheck -c Release
```

The tool runs 63 checks without game files and writes `artifacts/native-skin-check/checks.tsv`. Cases include direct draws, rigid effectors, global/remapped matrix palettes, slot 255 and node indices above 255, overlapping vertex ranges, per-corner overrides, unused vertices, multiple streams, raw/implicit weights and invalid ranges or skeletons. Ordinary geometry, UV sets, texture slots and render-style indices must remain unchanged. The release workflow runs these checks before packaging.

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
- `inspection.tsv`: whether vertex values were requested. A run without `--vertices` clears both detailed tables to their headers.

Skin hashes cover vertices in source order, each with four int32 indices then four float32 weights. Sorted corner overrides follow as an int32 corner index and four interleaved int32-node/float32-weight pairs. Unused slots have node `-1` and weight zero. Geometry hashes compare capture with the ordinary decoder and include positions, indices, all UV sets, texture slots and render style.

## Binding rules and limits

`LithTechModelDecoder.TryDecodeWithSkinning` is an optional internal entry point. It attaches `NativeSkeleton` and per-mesh `NativeSkinning`; ordinary preview and OBJ decoding do not allocate these arrays. Invalid skin data reports a per-mesh error while retaining static geometry. Invalid hierarchy data rejects capture. Reading through the hierarchy does not establish that the remaining file is valid.

Direct bone sets apply to the actual triangle indices between successive cumulative ends. Vertex bounds alone cannot assign a bone. Shared vertices with different draw bindings retain corner overrides for a future exporter to split. Each active draw uses its stored leading weights and an implicit final weight of `1 - sum(previous weights)`; finite values are retained without clamping or normalization. Matrix-palette bytes address slots directly, without adding the minimum. Remapped global node IDs can exceed 255, and palette slot 255 is valid when in range.

The [recorded 37-file check](../../docs/evidence/native-skin-20261105-validation.tsv) independently reproduces 160 meshes, 141,464 vertices, 150,648 triangles, 2,395 draw groups and all 2,146 existing node records. It covers 88 direct and 72 rigid meshes, with no corner overrides or weights outside the unit interval. Matrix palettes currently have synthetic coverage only. Physical motion-version selection, coordinate transforms, rendered poses and skeletal animation export remain unverified. OBJ continues to export geometry and textures.
