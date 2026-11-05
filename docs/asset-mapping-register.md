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
- `skeleton_or_bone_names` records a node count and hierarchy hash when inspected; the linked node table retains every name and parent. Counts include the scene root.
- `bone_or_socket_binding` records embedded model sockets or the raw CFT attachment target type. An embedded socket does not establish which accessory uses it. Blank values mean unknown, not absent.
- `motion_model_path` records a motion-model path declared in the same typed CFT row. It does not claim that OBJ export contains the animation.
- OBJ currently exports geometry and textures. It does not preserve skeleton bones or animation tracks.

## Snapshot

The register contains 31 exported slot records and three missing model declarations. These are recorded samples, not a full-game coverage count.

The 2026-11-05 FireAim Marker scan read all 482 REZ archives without errors and extracted 125 matching resources. Nine filtered REZ archives were rebuilt for focused exports; every extracted resource's bytes were checked against the original SHA-256. The CFT files have the same content hashes as the 2026-10-26 snapshot.

The 11 existing body, arm, and accessory models have 20 mesh-slot rows. Focused exports confirm all 20 rows and 20 distinct diffuse textures, with no skipped geometry. The CFT extractor now preserves the unnumbered base fields alongside `TexFile2`; BL hair slot 0 uses its explicitly declared Hair DTX and slot 1 uses Hair2. The three `_INDEXED` body paths declared in CFT were not found in the archives or as loose FireAim files.

The repository's [structure audit](https://github.com/LRolinx/CFRezManager/tree/main/tools/LtbStructureAudit) fully consumed 17 physical LTB files. The [model summaries](evidence/fireaim-marker-20261105-structure-models.tsv), [370 node records](evidence/fireaim-marker-20261105-skeleton-nodes.tsv), [animation records](evidence/fireaim-marker-20261105-animations.tsv), and [41 sockets](evidence/fireaim-marker-20261105-sockets.tsv) retain the parsed data. Body models have 29 nodes, arms have 46, and the five accessories have two each.

The body models and all four `M-motion_blue.ltb` copies have identical node indices, names, parents and flags. Their serialized bind matrices differ; matching hierarchy does not verify rendered animation or skinning. The four motion copies contain 1,499 (`rez`), 1,971 (`rez2`), 2,256 (`rez3`) and 2,775 (`rez4`) animations. The arm hierarchy differs from this row-level motion reference. The [comparison table](evidence/fireaim-marker-20261105-motion-compatibility.tsv) records both findings. CFT attachment target types 0, 4 and 5 are retained in [the raw fields](evidence/fireaim-marker-20261105-attachment-fields.tsv); their external socket names remain unresolved.

The comparison table also records name overlap and the animation limit of the pinned external RezView reader. Body pairs match all 29 node names; arm pairs share only `Scene Root`, with no matching non-root node. Every complete motion list exceeds that reader's 1,024-animation limit. The [source and protocol review](rezview-animation-reference.md) separates these checks from unverified playback. `any_name_gate_passes` evaluates a weak name-only condition; it does not report that the external program actually paired these files.

The [resource manifest](evidence/fireaim-marker-20261105-resources.tsv) records original archive paths, stored byte counts, MD5 and SHA-256. The [CFT fields](evidence/fireaim-marker-20261105-cft-links.tsv), [motion links](evidence/fireaim-marker-20261105-motion-links.tsv), [export slots](evidence/fireaim-marker-20261105-export-slots.tsv), and [guard checks](evidence/fireaim-marker-20261105-guards.tsv) preserve the evidence. Negative checks rejected an invalid DTX duplicate and inconsistent model-copy layouts. [Structure guards](evidence/fireaim-marker-20261105-structure-guards.tsv) reject four altered files without publishing their detailed metadata.
