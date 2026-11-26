# Third-party notices

The files below are bundled **unmodified** as embedded resources of `WSystem.EInvoice.dll` (source:
`src/WSystem.EInvoice/Resources/`). Their own licences apply to them; the Apache-2.0 licence of this package covers
W System's code only.

| Path (under `src/WSystem.EInvoice/Resources/`) | Component | Licence | Source |
|---|---|---|---|
| `kosit/xrechnung-3.0.2-2026-08-31/` | KoSIT validator configuration for XRechnung 3.0.2, release 2026-08-31 (XRechnung schematron/XSLT, scenarios) | Apache-2.0 (`kosit/LICENSE-KoSIT-Apache-2.0.txt`) | https://github.com/itplr-kosit/validator-configuration-xrechnung/releases/tag/v2026-08-31 (zip sha256 `2530cd107c414511c5d0462ec10f886910395abfca820db82e83d70bf01221a8`) |
| `.../EN16931-*-validation.xsl`, `.../resources/{ubl,cii}/**/xsl/EN16931-*` | CEN/TC 434 EN 16931 validation artefacts, reproduced with permission from CEN | **EUPL-1.2** (`kosit/LICENSE-CEN-EN16931-EUPL-1.2.txt`) | https://github.com/ConnectingEurope/eInvoicing-EN16931 |
| `.../resources/ubl/2.1/xsd/` | OASIS UBL 2.1 XML Schemas | OASIS IPR Policy (royalty-free) | http://docs.oasis-open.org/ubl/os-UBL-2.1/ |
| `.../resources/cii/16b/xsd/` | UN/CEFACT Cross Industry Invoice D16B XML Schemas | UNECE, free of charge | https://unece.org/trade/uncefact/xml-schemas |
| `fonts/Vera.z`, `fonts/VeraBd.z` (+ metrics `.json`) | Bitstream Vera Sans, embedded in the generated PDFs | Bitstream Vera licence (`fonts/bitstream-vera-license.txt`) | https://www.gnome.org/fonts/ |
| `sRGB2014.icc` | sRGB v2 ICC profile (PDF/A output intent) | ICC: may be copied and embedded freely | https://www.color.org/srgbprofiles.xalter |

If you change any EUPL-1.2 file, you must release your changed version under EUPL-1.2 (EUPL Art. 5). This package never changes them.

**Not included:** the OpenPeppol Peppol BIS Billing 3.0 schematron (PEPPOL-EN16931-*). Its terms do not allow redistribution without OpenPeppol's consent. The few Peppol-origin rules inside the KoSIT XRechnung configuration ship under KoSIT's Apache-2.0 release.

## Code

`src/WSystem.EInvoice/Pdf/PdfA3Writer.cs` contains a C# port of the subset of **FPDF 1.9** (Olivier Plathey) that the
invoice layout uses. FPDF licence: "Permission is hereby granted, free of charge, to any person obtaining a copy of this
software to use, copy, modify, distribute, sublicense, and/or sell copies of the software, and to permit persons to whom
the software is furnished to do so. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED."
http://www.fpdf.org/

No code from ZUGFeRD-csharp (or any other e-invoicing library) is included. `docs/MIGRATION-FROM-ZUGFERD-CSHARP.md`
only refers to that library's public API names to show the equivalent calls.

## Test data

`tests/WSystem.EInvoice.Tests/fixtures/validator/` holds public sample invoices from CEN, KoSIT and the Mustang project
under their own licences; see `fixtures/validator/NOTICE.md`. They are not part of the NuGet package.
