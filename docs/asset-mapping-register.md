# Asset Mapping Register

`asset-mapping-register.tsv` is a versioned reference record of confirmed model-to-texture slots and table-declared relationships. Published packages include it under `docs/`. The runtime resolver does not load this register; unverified rows do not change export behavior.

## Status

- `exported-slot`: the focused OBJ export's texture-slot report marked this model, mesh, and slot as exported.
- `table-declared-slot-unverified`: a typed CFT row declares the model and texture fields, but the model slot has not been verified by export.

Rows describe individual slots. A successful row can coexist with other unresolved meshes in the same OBJ export.

## Fields

- `model_path` and `texture_path` use paths relative to the game resource root.
- `global_texture_slot` is the LTB skin slot; it is blank until slot evidence exists.
- `mapping_source_or_field` names the resolver source for exported slots or the CFT field for declared relationships.
- `skeleton_or_bone_names` and `bone_or_socket_binding` are blank when they have not been inspected or confirmed. Blank values mean unknown, not absent.
- `motion_model_path` records a motion-model path declared in the same typed CFT row. It does not claim that OBJ export contains the animation.
- OBJ currently exports geometry and textures. It does not preserve skeleton bones or animation tracks.

## Snapshot

The register currently contains 11 individually exported slots and 14 table-declared relationships awaiting slot verification. The FireAim Marker entries come from the 2026-10-26 game-root audit of 482 REZ archives. They record CFT row references and motion-model paths for later export work. Its `CharacterItem.CFT` accessory rows are not yet slot-verified. The HuluXiongdi and DemonicBeast rows record successful individual texture-slot exports; unrelated unresolved slots from those larger exports are not promoted to confirmed mappings.
