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

The FireAim snapshot records 17 complete physical LTB files. Checks using a truncated file, invalid node count, unsupported version and unexpected trailing bytes reject all four altered files and emit detailed rows only for the valid control.
