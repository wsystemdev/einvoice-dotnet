# WSystem.EInvoice: EU e-invoices in .NET, offline

Create, check and read XRechnung 3.0 (UBL and CII), EN 16931 UBL/CII, Peppol BIS 3 structure and ZUGFeRD / Factur-X
PDF/A-3b invoices inside your own .NET application. No API key, no network. Apache-2.0.

> **Status: beta (0.9.0-beta.1).** The API may change before v1.0. Until the package is on NuGet, build it from this
> repository (see below).

- .NET 8 and .NET Standard 2.0 (so .NET Framework 4.6.2+ and Mono work too). One dependency on .NET Standard: System.Text.Json.
- Same input and the same XML as the W System API's `POST /einvoice/create`, byte for byte (tested on 439 fixtures).
- Totals, VAT breakdown and rounding are computed for you (EN 16931 rules), including allowances, charges, credit notes and prepaid amounts.
- ZUGFeRD 2.3 / Factur-X 1.07 hybrids: a readable A4 PDF/A-3b invoice with the CII XML embedded (`factur-x.xml` / `xrechnung.xml`).
- Validation: XML Schema plus the bundled, unmodified official KoSIT / CEN EN 16931 and XRechnung 3.0.2 rule files.
- Moving off ZUGFeRD-csharp (maintenance ends 30 Nov 2026)? See [docs/MIGRATION-FROM-ZUGFERD-CSHARP.md](docs/MIGRATION-FROM-ZUGFERD-CSHARP.md).

## Install

```bash
dotnet add package WSystem.EInvoice --prerelease
```

Until it is published, build the package yourself: `dotnet pack src/WSystem.EInvoice -c Release -o artifacts`, then
`dotnet add package WSystem.EInvoice --prerelease --source ./artifacts`.

## Create an invoice

```csharp
using WSystem.EInvoice;

var invoice = new Invoice
{
    Format = "xrechnung",   // en16931 | xrechnung | peppol-bis3 | cii | xrechnung-cii | zugferd | factur-x | zugferd-xrechnung
    InvoiceNumber = "INV-2026-001",
    IssueDate = new DateTime(2026, 10, 18),
    DueDate = new DateTime(2026, 11, 17),
    Currency = "EUR",
    BuyerReference = "04011000-12345-34",      // Leitweg-ID for German public buyers
    Seller = new Party { Name = "Muster GmbH", VatId = "DE123456789", Street = "Hauptstr. 1", City = "Berlin",
        PostalCode = "10115", Country = "DE", Email = "billing@muster.de", Phone = "+49 30 1234", ContactName = "Anna Muster" },
    Buyer = new Party { Name = "Kunde AG", VatId = "DE811193231", Street = "Ring 2", City = "Muenchen",
        PostalCode = "80331", Country = "DE", Email = "ap@kunde.de" },
    Lines = { new InvoiceLine { Description = "Consulting", Quantity = 10, Unit = "HUR", UnitPrice = 95.50m, VatRate = 19 } },
    Payment = new Payment { Iban = "DE89370400440532013000", Terms = "30 days net" },
};

var result = new EInvoice().Create(invoice);
if (result.Valid == true) result.Save("invoice.xml");
else foreach (var e in result.Errors) Console.WriteLine($"{e.RuleId}: {e.Message} {e.Fix}");
```

`Create` also takes the API's JSON body as a string (`Create(json)`) or a dictionary of the same shape.

For `zugferd`, `factur-x` and `zugferd-xrechnung`, `result.Pdf` holds the PDF/A-3b hybrid (built only when the XML is
valid). Credit notes (`TypeCode = "381"` plus `PrecedingInvoice`) and allowances or charges on lines or the document
appear in both the XML and the visual PDF. `result.Totals` has `line_total`, `vat_total`, `total_with_vat` and `payable`.

## Validate an incoming file

```csharp
var r = new EInvoice().Validate(File.ReadAllBytes("incoming.pdf"));   // UBL, CII, or a ZUGFeRD / Factur-X / XRechnung PDF
Console.WriteLine($"{r.Valid} [{r.Engine}, {r.Conformance}] {r.RuleSet}");
foreach (var e in r.Errors) Console.WriteLine($"{e.RuleId} ({string.Join(",", e.BusinessTerms)}): {e.Message}");
```

The result has the same fields as the API's `POST /einvoice/validate` (`r.ToJson()` writes them). Slovak and Norwegian
national field checks run as warnings when the seller is in SK or NO.

## Read an incoming file

```csharp
var data = Parser.Parse(File.ReadAllBytes("incoming.xml"));   // same shape as POST /einvoice/parse
var inv = (Dictionary<string, object?>)data["invoice"]!;
Console.WriteLine($"{inv["invoice_number"]}: {((Dictionary<string, object?>)inv["totals"]!)["amount_due"]}");
```

## Which rules run where

| Engine | How | XML Schema | EN 16931 / XRechnung 3.0.2 business rules | Labels in every result |
|---|---|---|---|---|
| `dotnet-core` (default) | nothing to install, offline | yes | pre-check: a pure-.NET engine runs the bundled official rule files; it is not the official engine | `Engine = "dotnet-core"`, `Conformance = "pre-check"`, `Checked["EN16931"] = "core-subset"` |
| `remote` | `new EInvoice(new HttpValidator(apiKey: ...))`; opt-in, the file is sent to the W System API or a self-hosted W System validator | yes | official | `Engine = "remote"`, `Conformance = "official"` |

A valid result from `dotnet-core` is a pre-check, not a conformance check. On the test corpus it gives the same rule
ids, messages and locations as the official engine, but only the official engine is the reference. Peppol BIS 3
rules are not included (the Peppol-origin rules inside the XRechnung configuration do run).

```csharp
using WSystem.EInvoice.Validation;

var official = new EInvoice(new HttpValidator(apiKey: Environment.GetEnvironmentVariable("WSYSTEM_API_KEY")));
var check = official.Validate(File.ReadAllBytes("invoice.xml"));   // keyless works within the free tier
```

## How it is tested

- Generator: byte-identical XML and totals to the W System API on 439 fixtures (UBL, CII, XRechnung, Peppol structure, ZUGFeRD, credit notes, allowances, rounding edge cases).
- Validator: same verdict, rule ids, severities, messages, locations and business terms as the W System reference engine on 195 public files (CEN, KoSIT, Mustang, W System), plus the shared 17-file validator corpus; XPath engine checked against 278 answers recorded from Saxon.
- PDF: every hybrid has the same objects and page content as the W System PHP SDK's PDF, and round-trips through the validator.
- CI runs the official tools on every generated file: KoSIT validator 1.6.3 (XRechnung), veraPDF 1.30 (PDF/A-3b) and Mustang 2.26 (ZUGFeRD / Factur-X).

## Licence

Apache-2.0. Bundled rule files, fonts and the ICC profile keep their own licences (CEN EN 16931 artefacts: EUPL-1.2);
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). A technically valid file is not tax or legal advice.
