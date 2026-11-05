# Asset Mapping Register

`asset-mapping-register.tsv` is a versioned reference record of confirmed model-to-texture slots and table-declared relationships. Published packages include it under `docs/`. The runtime resolver does not load this register; unverified rows do not change export behavior.

## Status

- `exported-slot`: the focused OBJ export's texture-slot report marked this model, mesh, and slot as exported.
- `exported-slot-unverified`: the export wrote a texture, but conflicting physical copies or multiple side-specific fields leave the intended binding unconfirmed. The written texture must not be treated as a confirmed game mapping.
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

The register contains 533 confirmed exported slot records, five written but unverified slots, two unresolved slots, eight declarations without a matching decoded model slot, and three missing model declarations. These are recorded samples, not a full-game coverage count. Repeated physical model copies are retained in the evidence tables and deduplicated in the register.

### CharacterItem base fields

The 2026-11-05 batch covers 260 typed model rows from 174 CharacterItem records and 228 distinct model paths. It scanned all 482 REZ archives without errors, extracted 759 physical resources, and verified their stored bytes after rebuilding 12 filtered archives. The [resource manifest](evidence/characteritem-base-20261105-resources.tsv) and [736 requested paths](evidence/characteritem-base-20261105-targets.tsv) retain archive identities, hashes and missing paths.

Base fields can reference exact DTX paths anywhere under `ModelTextures/`, including `Character` and `Wave`. Binding still requires one unambiguous base path, slot 0 in every decoded model copy, matching slot layouts, and decodable texture copies with identical bytes or equivalent decoded pixels. Multiple-slot models also require a numbered texture field.

Seven [export jobs](evidence/characteritem-base-20261105-jobs.tsv) produced 523 physical mesh-slot rows: 516 confirmed, five written but unverified, and two unresolved. Nineteen formerly unresolved rows now export; all 502 baseline written rows retain identical PNG SHA-256. One existing mesh-derived binding now uses its explicit CFT field. Every one of the 521 written rows matches the decoded pixel hash of a recorded source DTX. The [slot evidence](evidence/characteritem-base-20261105-export-slots.tsv) records the exact CFT row, physical model source, selected texture copies, pixel hash and validation status.

The [486 physical DTX records](evidence/characteritem-base-20261105-texture-copies.tsv) expose three paths with different decoded pixels across archive versions: CFPL GR headset, The_Fates_Casual GR hair and GR hat. The hair base remains unresolved. Four numbered headset/hat exports are marked unverified. A MasterHERO hair fallback is also unverified because its CFT row declares different GR and BL base textures; the corresponding pouch remains unresolved. No new explicit base binding was added for either ambiguous row.

Five requested DTX paths were absent from all REZ archives. They belong to numbered fields for which these decoded models have no slot. They remain declarations, rather than export failures or inferred bindings. The [260 CFT mappings](evidence/characteritem-base-20261105-cft-links.tsv) retain every original field.

The [structure inventory](evidence/characteritem-base-20261105-structure-models.tsv) completely parsed 272 physical LTB files, including related models: [544 nodes](evidence/characteritem-base-20261105-structure-nodes.tsv), [272 embedded animations](evidence/characteritem-base-20261105-structure-animations.tsv), and [zero embedded sockets](evidence/characteritem-base-20261105-structure-sockets.tsv). Each has two nodes and one embedded animation. Raw CFT attachment fields are retained [separately](evidence/characteritem-base-20261105-attachment-fields.tsv); their target-type enums do not establish an external bone binding. [Guard results](evidence/characteritem-base-20261105-guards.tsv) include an invalid duplicate Character DTX that blocks base slot 0 while retaining numbered slot 1.

### FireAim Marker

The 2026-11-05 FireAim Marker scan read all 482 REZ archives without errors and extracted 125 matching resources. Nine filtered REZ archives were rebuilt for focused exports; every extracted resource's bytes were checked against the original SHA-256. The CFT files have the same content hashes as the 2026-10-26 snapshot.

The 11 existing body, arm, and accessory models have 20 mesh-slot rows. Focused exports confirm all 20 rows and 20 distinct diffuse textures, with no skipped geometry. The CFT extractor now preserves the unnumbered base fields alongside `TexFile2`; BL hair slot 0 uses its explicitly declared Hair DTX and slot 1 uses Hair2. The three `_INDEXED` body paths declared in CFT were not found in the archives or as loose FireAim files.

The repository's [structure audit](https://github.com/LRolinx/CFRezManager/tree/main/tools/LtbStructureAudit) fully consumed 17 physical LTB files. The [model summaries](evidence/fireaim-marker-20261105-structure-models.tsv), [370 node records](evidence/fireaim-marker-20261105-skeleton-nodes.tsv), [animation records](evidence/fireaim-marker-20261105-animations.tsv), and [41 sockets](evidence/fireaim-marker-20261105-sockets.tsv) retain the parsed data. Body models have 29 nodes, arms have 46, and the five accessories have two each.

The body models and all four `M-motion_blue.ltb` copies have identical node indices, names, parents and flags. Their serialized bind matrices differ; matching hierarchy does not verify rendered animation or skinning. The four motion copies contain 1,499 (`rez`), 1,971 (`rez2`), 2,256 (`rez3`) and 2,775 (`rez4`) animations. The arm hierarchy differs from this row-level motion reference. The [comparison table](evidence/fireaim-marker-20261105-motion-compatibility.tsv) records both findings. CFT attachment target types 0, 4 and 5 are retained in [the raw fields](evidence/fireaim-marker-20261105-attachment-fields.tsv); their external socket names remain unresolved.

The comparison table also records name overlap. Body pairs match all 29 node names; arm pairs share only `Scene Root`, with no matching non-root node. Shared names alone do not establish a valid animation binding.

The [resource manifest](evidence/fireaim-marker-20261105-resources.tsv) records original archive paths, stored byte counts, MD5 and SHA-256. The [CFT fields](evidence/fireaim-marker-20261105-cft-links.tsv), [motion links](evidence/fireaim-marker-20261105-motion-links.tsv), [export slots](evidence/fireaim-marker-20261105-export-slots.tsv), and [guard checks](evidence/fireaim-marker-20261105-guards.tsv) preserve the evidence. Negative checks rejected an invalid DTX duplicate and inconsistent model-copy layouts. [Structure guards](evidence/fireaim-marker-20261105-structure-guards.tsv) reject four altered files without publishing their detailed metadata.
