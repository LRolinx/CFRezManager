# HuluXiongdi WOMAN_GR slot audit

## Result

`Object05` on `PV-HuluXiongdi_WOMAN_GR` and `PV-HuluXiongdi_chg_WOMAN_GR` is the arm mesh at slot 1. Its name, UV coordinates, and triangle indices match the arm-role mesh in `Arm_WOMAN_GR.LTB` from `rez4/RF016.REZ`; both meshes have 206 vertices and 268 triangles. Their model-space vertex positions differ, so the cross-layer check uses the exact named UV-triangle signature.

With slot 1 excluded as an arm slot, LTC ordinal 0 resolves to slot 2. Focused exports confirmed:

| Model | Item context | Slot 2 texture |
| --- | --- | --- |
| `PV-HuluXiongdi_chg_WOMAN_GR` | 2589 | `PV-HuluXiongdi_Chg.DTX` |
| `PV-HuluXiongdi_chg_WOMAN_GR` | 2595 | `PV-HuluXiongdi.DTX` |
| `PV-HuluXiongdi_WOMAN_GR` | 2591 | `PV-HuluXiongdi_Chg.DTX` |
| `PV-HuluXiongdi_WOMAN_GR` | 2612 | `PV-HuluXiongdi.DTX` |

The hand slot 0 and arm slot 1 still have no resolved texture. The base-family `Hulu01` / `Hulu02` slot 3 maps to `PV-HuluXiongdi01.DTX`; the chg-family slot 3 remains unresolved. All four target exports decoded five meshes with no skipped geometry.

## Export reports

The four per-slot TSVs and their CLI summaries are in `artifacts/mapping-audit/hulu-crossroot-arm-fallback-20261026/`. The TSVs are named `hulu-chg-woman-gr-item-2589_texture_slots.tsv`, `hulu-chg-woman-gr-item-2595_texture_slots.tsv`, `hulu-base-woman-gr-item-2591_texture_slots.tsv`, and `hulu-base-woman-gr-item-2612_texture_slots.tsv`.
