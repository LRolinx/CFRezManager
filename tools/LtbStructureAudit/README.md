# LTB structure audit

Run from the repository with the .NET 8 SDK on Windows:

```powershell
dotnet run --project tools/LtbStructureAudit -c Release -- C:\Extracted C:\Reports\LtbStructure
```

The input is an extracted resource directory. The tool reads loose `.ltb` files recursively. If the directory contains a six-column `resource-manifest.tsv` (`archive`, `resource_path`, `size`, `archive_md5`, `sha256`, `extracted_path`), it uses those original source labels and checks each file's stored SHA-256.

The validated layout is native D3D LTB header version 9, model version 25, including an optional LZMA wrapper. Each file is bounded to 128 MiB. Other layouts produce an error row.

## Output

- `models.tsv`: source identity, file hashes, allocation and parsed counts, hierarchy/bind-matrix hashes, parse status and error.
- `nodes.tsv`: node index, parent index, name, flags and the serialized global bind matrix.
- `animations.tsv`: per-file animation names, compression types, interpolation times, keyframe counts and final keyframe times. Names repeated in different physical model copies remain separate records.
- `sockets.tsv`: embedded socket names, target node indices and serialized rotation/position/scale values.

Matrix and socket values are little-endian float32 bytes encoded as hexadecimal. Hierarchy hashes cover node index, parent, name and flags sorted by index. Bind-matrix hashes compare serialized values, so different hashes do not by themselves prove incompatible poses.

`structure-complete` requires the declared node, piece, LOD, weight-set, child-model, animation and socket counts to agree, bounded animation channels, and complete file consumption with at most three zero padding bytes. Detailed node, animation and socket rows are emitted only for complete files. Exit code 1 reports rejected files; exit code 2 reports invalid arguments.

The tool inventories animation channels without evaluating them. Matching node hierarchies is evidence for an association; animation playback, skin weights and retargeting still require validation. OBJ export remains geometry and texture export.

For CFT source fields referenced by the mapping records, v1.2.7 opens the complete decoded table as a grid with row numbers, numeric sorting and a frozen `RecordId` column. Export XLSX from that window or with `--export-cft-xlsx --input <file.cft> --output <file.xlsx>`. Preview sorting does not change the exported source order. The workbook's `RecordId` column is the serialized identifier, not a zero-based row index.

The FireAim snapshot records 17 complete physical LTB files. Checks using a truncated file, invalid node count, unsupported version and unexpected trailing bytes reject all four altered files and emit detailed rows only for the valid control.

The CharacterItem base-field snapshot records 272 complete physical LTB files, including related models. Each contains two nodes, one embedded animation and no embedded socket. The [mapping register guide](../../docs/asset-mapping-register.md) links their source hashes, 544 node records, 272 animation records, texture-copy checks and export results.

The CharacterItem side snapshot adds a separate inventory of 91 physical files, which can overlap the base snapshot. It records 182 nodes, 91 embedded animations and one socket: `Effect` on node 1 of `Models/WAVE/ETC/IFF_Shoulder.LTB`. The [node](../../docs/evidence/characteritem-side-20261105-structure-nodes.tsv), [animation](../../docs/evidence/characteritem-side-20261105-structure-animations.tsv) and [socket](../../docs/evidence/characteritem-side-20261105-structure-sockets.tsv) tables retain every parsed entry. The [raw CFT fields](../../docs/evidence/characteritem-side-20261105-raw-fields.tsv) cover 140 rows; target-type enums do not establish external socket bindings. v1.2.8 also keeps GR/BL mapping contexts separate when their model paths are identical.

v1.2.9 preserves these structure records while rejecting conflicting texture copies during export. The [texture-copy inventory](../../docs/evidence/texture-copy-20261105-variants.tsv) records the six affected physical DTX files; the [copy-check tool](../TextureCopyCheck/README.md) tests pixel equivalence and fallback behavior separately from model structure parsing.

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
