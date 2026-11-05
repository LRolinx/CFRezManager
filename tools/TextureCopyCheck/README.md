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
