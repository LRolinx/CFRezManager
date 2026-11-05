# Asset Mapping Register

`asset-mapping-register.tsv` is a versioned reference record of confirmed model-to-texture slots and table-declared relationships. Published packages include it under `docs/`. The runtime resolver does not load this register; unverified rows do not change export behavior.

## Status

- `exported-slot`: the focused OBJ export's texture-slot report marked this model, mesh, and slot as exported.
- `table-declared-slot-unverified`: a typed CFT row declares the model and texture fields, but the model slot has not been verified by export.
- `model-resource-missing`: the table declares the model, but the current resource scan did not find its file. Any listed texture and motion paths remain table declarations.
- `slot-unresolved`: the model and slot were decoded, but the focused export did not establish a texture binding.

Rows describe individual slots. A successful row can coexist with other unresolved meshes in the same OBJ export.

## Fields

- `model_path` and `texture_path` use paths relative to the game resource root.
- `global_texture_slot` is the LTB skin slot; it is blank until slot evidence exists.
- `mapping_source_or_field` names the resolver source for exported slots or the CFT field for declared relationships.
- `skeleton_or_bone_names` and `bone_or_socket_binding` are blank when they have not been inspected or confirmed. Blank values mean unknown, not absent.
- `motion_model_path` records a motion-model path declared in the same typed CFT row. It does not claim that OBJ export contains the animation.
- OBJ currently exports geometry and textures. It does not preserve skeleton bones or animation tracks.

## Snapshot

The register contains 30 exported slot records, three missing model declarations, and one unresolved slot. These are recorded samples, not a full-game coverage count.

The 2026-11-05 FireAim Marker scan read all 482 REZ archives without errors and extracted 125 matching resources. Nine filtered REZ archives were rebuilt for focused exports; every extracted resource's bytes were checked against the original SHA-256. The CFT files have the same content hashes as the 2026-10-26 snapshot.

The 11 existing body, arm, and accessory models have 20 mesh-slot rows. Focused exports confirm 19 rows and 19 distinct diffuse textures, with no skipped geometry. Ten models have every reported slot exported. The BL hair model's primary slot 0 remains unresolved; its slot 1 exports the CFT-declared Hair2 DTX. The three `_INDEXED` body paths declared in CFT were not found in the archives or as loose FireAim files.

`M-motion_blue.ltb` has four physical copies with different hashes. The table's motion reference is retained, but skeleton and animation compatibility have not been established. Bone names and socket bindings remain blank.

The [resource manifest](evidence/fireaim-marker-20261105-resources.tsv) records original archive paths, stored byte counts, MD5 and SHA-256. The [CFT fields](evidence/fireaim-marker-20261105-cft-links.tsv), [motion links](evidence/fireaim-marker-20261105-motion-links.tsv), [export slots](evidence/fireaim-marker-20261105-export-slots.tsv), and [guard checks](evidence/fireaim-marker-20261105-guards.tsv) preserve the evidence. Negative checks rejected an invalid DTX duplicate and inconsistent model-copy slot layouts.
