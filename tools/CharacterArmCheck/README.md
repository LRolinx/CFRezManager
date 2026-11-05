# Character arm context checks

Run from the repository on Windows with the .NET 8 SDK:

```powershell
dotnet run --project tools/CharacterArmCheck -c Release
```

The tool creates a native two-piece triangle LTB, a two-row CharacterType CFT and eight small PNG textures under a unique `artifacts/character-arm-check/` directory. No game files are required. Both sides deliberately share a model whose filename ends in `_GR`; the declared model columns must retain both GR and BL skins.

The 67 checks cover side-qualified row, identity and positive RecordId selectors, reused models with different row skins, ambiguous unqualified contexts, absent selectors and the distinction between row indices and record identifiers, reversed hand/arm slots, deferred fallback indexing, early success without further name guesses and retained SGFX auxiliary output. Four ordinary command-line OBJ exports must use the declared fields and preserve every texture pixel. Results default to `artifacts/character-arm-check/results.tsv`; `--output <report.tsv>` selects another path.

The release workflow runs these checks before packaging. [Texture-copy checks](../TextureCopyCheck/README.md) separately verify conflicting physical resources; [structure checks](../LtbStructureAudit/README.md) cover nodes, child declarations, bindings and animation blend masks. These checks do not validate animation playback or vertex skinning.
