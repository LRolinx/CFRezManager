# WildWest SP arm binding audit

## Result

`Arm_WildWest_SP` remains unmapped. The current inventory contains one model at `Models/PLAYERVIEW/ArmModel/Arm_WildWest_SP.LTB` in `rez4/RF016.REZ` (81,152 bytes; MD5 `9C2DC9D2A8982684B77B44F36BAFA77F`). No CFT model, related-model, or texture field references this model or a `WildWest_SP` texture.

The player-view texture inventory contains eight WildWest hand/arm DTX paths. They are the BL and GR hand/arm textures for the CFT-registered BLGR and Santa characters; no SP hand/arm DTX path is present. The SP model's `default` and `Fview-Arm` meshes have no embedded texture path references. Exporting under character A9350 leaves both slots missing.

Neither SP mesh has an exact UV, index, and geometry signature match in the audited arm-model set. The available BLGR and Santa textures therefore cannot be transferred to SP based on the current evidence.

## Evidence files

- `arm-family-evidence-20261024/resources.tsv`: model and player-view DTX inventory.
- `arm-family-evidence-20261024/meshes.tsv`: decoded mesh counts and UV/index/geometry signatures.
- `cft-model-links-20261023.tsv`: decoded CFT model, related-model, and texture fields; no SP reference.
- `arm-family-evidence-20261024/Arm_WildWest_SP_A9350_texture_report.txt`: focused export result with two missing slots.
- `arm-family-evidence-20261024/context-export-results.tsv`: per-context texture counts.
