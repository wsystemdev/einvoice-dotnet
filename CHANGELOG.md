# Changelog

## Unreleased

### Validation

- The severity levels of the official KoSIT XRechnung scenarios (`customLevel` in `scenarios.xml`, config 3.0.2) are now applied for standard XRechnung and XRechnung CVD, as KoSIT does, not only for XRechnung Extension (same tables as the W System engine 0.3.157.1). The official CVD samples (BR-CL-13) and unit codes outside the BR-CL-23 list in standard XRechnung are now accepted with a warning.
- **Customer-visible, stricter (matches KoSIT):** in standard XRechnung, CII-SR-452, CII-SR-453, CII-SR-454, CII-SR-465, CII-SR-466 and (UBL invoices) UBL-CR-646 now count as errors, so files that break them now fail. Example: Mustang's `XRECHNUNG_Einfach_fx.pdf` (two BT-41 elements, CII-SR-465), which the KoSIT validator rejects too.
- Shared corpus synced from the W System validator (4 new fixtures; `XRECHNUNG_Einfach_fx.pdf` moved to `invalid/`), golden results regenerated from the W System engine.

## 0.9.0-beta.2

- Decompressed PDF data is capped at 75 MB per file (decompression-bomb fix). See the release notes of 0.9.0-beta.2.
