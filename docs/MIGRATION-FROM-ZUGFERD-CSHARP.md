# Migrating from ZUGFeRD-csharp to WSystem.EInvoice

ZUGFeRD-csharp ([stephanstapel/ZUGFeRD-csharp](https://github.com/stephanstapel/ZUGFeRD-csharp), Apache-2.0; namespace `s2industries.ZUGFeRD`) stops receiving maintenance on 30 November 2026. This guide
maps its `InvoiceDescriptor` API to WSystem.EInvoice, with code side by side. WSystem.EInvoice is an independent
implementation; it contains no ZUGFeRD-csharp code. Both libraries are Apache-2.0.

## The main differences

| | ZUGFeRD-csharp | WSystem.EInvoice |
|---|---|---|
| Model | `InvoiceDescriptor` filled through `Set…` / `Add…` calls | plain object `Invoice` (`Party`, `InvoiceLine`, `AllowanceCharge`, `Payment`), or the same data as JSON |
| Totals and VAT breakdown | you compute them and pass them in (`SetTotals`, `AddApplicableTradeTax`) | **computed for you** from the lines, allowances, charges and prepaid amount, with EN 16931 rounding |
| Output format | `Save(…, ZUGFeRDVersion, Profile, ZUGFeRDFormats)` | one string: `Format = "xrechnung"`, `"xrechnung-cii"`, `"en16931"`, `"cii"`, `"peppol-bis3"`, `"zugferd"`, `"factur-x"`, `"zugferd-xrechnung"` |
| PDF hybrid | separate package `ZUGFeRD.PDF`; you supply the visual PDF | built in: the PDF/A-3b invoice page is rendered for you and the XML embedded |
| Validation | `InvoiceValidator` (a set of library checks) | XML Schema + the official KoSIT / CEN rule files, offline (`dotnet-core` pre-check), or the official engine via `HttpValidator` |
| Reading | `InvoiceDescriptor.Load` returns a descriptor | `Parser.Parse` returns a dictionary (same shape as the W System API's `POST /einvoice/parse`) |
| Codes | C# enums (`CountryCodes.DE`, `QuantityCodes.HUR`, `TaxCategoryCodes.S`) | the code strings themselves (`"DE"`, `"HUR"`, `"S"`) |

## A complete invoice, side by side

**ZUGFeRD-csharp**

```csharp
using s2industries.ZUGFeRD;

var desc = InvoiceDescriptor.CreateInvoice("INV-2026-001", new DateTime(2026, 10, 18), CurrencyCodes.EUR);
desc.ReferenceOrderNo = "04011000-12345-34";                       // BT-10 buyer reference / Leitweg-ID
desc.AddNote("Thank you for your order.");
desc.SetSeller("Muster GmbH", "10115", "Berlin", "Hauptstr. 1", CountryCodes.DE);
desc.SetSellerContact("Anna Muster", "", "billing@muster.de", "+49 30 1234");
desc.AddSellerTaxRegistration("DE123456789", TaxRegistrationSchemeID.VA);
desc.AddSellerTaxRegistration("201/113/40209", TaxRegistrationSchemeID.FC);
desc.SetSellerElectronicAddress("billing@muster.de", ElectronicAddressSchemeIdentifiers.ElectronicMailSmtp);
desc.SetBuyer("Kunde AG", "80331", "Muenchen", "Ring 2", CountryCodes.DE);
desc.SetBuyerContact("Accounts payable", "", "ap@kunde.de");
desc.AddBuyerTaxRegistration("DE811193231", TaxRegistrationSchemeID.VA);
desc.SetBuyerElectronicAddress("ap@kunde.de", ElectronicAddressSchemeIdentifiers.ElectronicMailSmtp);
desc.SetBuyerOrderReferenceDocument("PO-4711");

desc.AddTradeLineItem(name: "Consulting", netUnitPrice: 95.50m, unitCode: QuantityCodes.HUR,
    billedQuantity: 10m, lineTotalAmount: 955.00m,
    taxType: TaxTypes.VAT, categoryCode: TaxCategoryCodes.S, taxPercent: 19m);

// you compute the VAT breakdown and every total yourself
desc.AddApplicableTradeTax(955.00m, 19m, 181.45m, TaxTypes.VAT, TaxCategoryCodes.S);
desc.SetTotals(lineTotalAmount: 955.00m, chargeTotalAmount: 0m, allowanceTotalAmount: 0m,
    taxBasisAmount: 955.00m, taxTotalAmount: 181.45m, grandTotalAmount: 1136.45m,
    totalPrepaidAmount: 0m, duePayableAmount: 1136.45m);

desc.SetPaymentMeans(PaymentMeansTypeCodes.SEPACreditTransfer, "INV-2026-001");
desc.AddCreditorFinancialAccount("DE89370400440532013000", "COBADEFFXXX");
desc.AddTradePaymentTerms("30 days net", new DateTime(2026, 11, 17));

desc.Save("invoice.xml", ZUGFeRDVersion.Version23, Profile.XRechnung, ZUGFeRDFormats.UBL);
```

**WSystem.EInvoice**

```csharp
using WSystem.EInvoice;

var invoice = new Invoice
{
    Format = "xrechnung",                                          // UBL; "xrechnung-cii" for CII
    InvoiceNumber = "INV-2026-001",
    IssueDate = new DateTime(2026, 10, 18),
    DueDate = new DateTime(2026, 11, 17),
    Currency = "EUR",
    BuyerReference = "04011000-12345-34",
    OrderReference = "PO-4711",
    Note = "Thank you for your order.",
    Seller = new Party
    {
        Name = "Muster GmbH", Street = "Hauptstr. 1", PostalCode = "10115", City = "Berlin", Country = "DE",
        ContactName = "Anna Muster", Email = "billing@muster.de", Phone = "+49 30 1234",
        VatId = "DE123456789", TaxNumber = "201/113/40209",
        // electronic address defaults to Email with scheme EM; set EndpointId / EndpointScheme to override
    },
    Buyer = new Party
    {
        Name = "Kunde AG", Street = "Ring 2", PostalCode = "80331", City = "Muenchen", Country = "DE",
        ContactName = "Accounts payable", Email = "ap@kunde.de", VatId = "DE811193231",
    },
    Lines = { new InvoiceLine { Description = "Consulting", Quantity = 10, Unit = "HUR", UnitPrice = 95.50m, VatRate = 19 } },
    Payment = new Payment
    {
        MeansCode = "58",                                          // default: 58 when an IBAN is given
        Iban = "DE89370400440532013000", Bic = "COBADEFFXXX",
        Reference = "INV-2026-001", Terms = "30 days net",
    },
};

var result = new EInvoice().Create(invoice);                       // totals, VAT breakdown and validation included
if (result.Valid != true)
    foreach (var e in result.Errors) Console.WriteLine($"{e.RuleId}: {e.Message} {e.Fix}");
result.Save("invoice.xml");
Console.WriteLine(result.Totals["payable"]);                     // 1136.45
```

## Method by method

| ZUGFeRD-csharp | WSystem.EInvoice |
|---|---|
| `InvoiceDescriptor.CreateInvoice(invoiceNo, invoiceDate, CurrencyCodes.EUR)` | `new Invoice { InvoiceNumber = …, IssueDate = …, Currency = "EUR" }` |
| `desc.Type = InvoiceType.CreditNote` | `TypeCode = "381"` (default `"380"`) |
| `desc.ReferenceOrderNo` (BT-10) | `BuyerReference` |
| `desc.ActualDeliveryDate` | `DeliveryDate` |
| `desc.AddNote(text)` | `Note` (one free-text note) |
| `SetSeller(name, postcode, city, street, country, id, globalID, legalOrganization…)` | `Seller = new Party { Name, PostalCode, City, Street, Country, CompanyId, CompanyIdScheme, LegalForm, TradingName }` |
| `SetBuyer(…)` | `Buyer = new Party { … }` |
| `SetSellerContact(name, orgunit, email, phone)` / `SetBuyerContact(…)` | `ContactName`, `Email`, `Phone` on the `Party` |
| `AddSellerTaxRegistration(no, TaxRegistrationSchemeID.VA)` | `VatId` |
| `AddSellerTaxRegistration(no, TaxRegistrationSchemeID.FC)` | `TaxNumber` |
| `SetSellerElectronicAddress(address, scheme)` / `SetBuyerElectronicAddress(…)` | `EndpointId`, `EndpointScheme` (e.g. `"9930"`, `"0088"`; defaults to `Email` with `"EM"`) |
| `AddTradeLineItem(name, netUnitPrice, unitCode, description, …, billedQuantity, …, categoryCode, taxPercent)` | `Lines.Add(new InvoiceLine { Description = name, ItemDescription = description, UnitPrice, Unit, Quantity, VatCategory, VatRate, SellerItemId })` |
| `lineTotalAmount:` on a line | computed (quantity × price − line allowances + line charges) |
| `TradeLineItem.AddTradeAllowance(…)` / `AddTradeCharge(…)` on a line | `InvoiceLine.Allowances` / `InvoiceLine.Charges` |
| `AddTradeAllowance(basis, currency, amount, reason, …, taxPercent, reasonCode)` | `Allowances = { new AllowanceCharge { Amount or Percent + BaseAmount, Reason, ReasonCode, VatRate, VatCategory } }` |
| `AddTradeCharge(…)` / `AddLogisticsServiceCharge(…)` | `Charges = { new AllowanceCharge { … } }` |
| `AddApplicableTradeTax(basis, percent, taxAmount, type, category, …, exemptionReasonCode, exemptionReason)` | computed; exemption text and code per category via `VatExemptionReason` / `VatExemptionCode`, e.g. `{ ["AE"] = "Reverse charge" }` |
| `SetTotals(lineTotal, chargeTotal, allowanceTotal, taxBasis, taxTotal, grandTotal, prepaid, duePayable)` | computed; only `PrepaidAmount` is an input. Read them from `result.Totals` |
| `SetPaymentMeans(PaymentMeansTypeCodes.SEPACreditTransfer, information)` | `Payment.MeansCode = "58"`, `Payment.Reference` |
| `AddCreditorFinancialAccount(iban, bic, …, name)` | `Payment.Iban`, `Payment.Bic`, `Payment.AccountName` |
| `AddTradePaymentTerms(description, dueDate)` | `Payment.Terms`, `DueDate` |
| `AddInvoiceReferencedDocument(id, issueDate)` (credit notes) | `PrecedingInvoice`, `PrecedingInvoiceDate` |
| `SetBuyerOrderReferenceDocument(orderNo)` | `OrderReference` |
| `Save(stream or filename, version, Profile.XRechnung, ZUGFeRDFormats.UBL)` | `Format = "xrechnung"`; `result.Xml` (string) or `result.Save(path)` |
| `Save(…, Profile.XRechnung, ZUGFeRDFormats.CII)` | `Format = "xrechnung-cii"` |
| `Save(…, Profile.Comfort, ZUGFeRDFormats.CII)` (EN 16931) | `Format = "cii"`; UBL: `"en16931"` |
| `InvoicePdfProcessor.SaveToPdf(target, version, Profile.Comfort, ZUGFeRDFormats.CII, sourcePdf, desc)` | `Format = "zugferd"` (or `"factur-x"`), then `result.Pdf` or `result.Save("invoice.pdf")`: a PDF/A-3b invoice page with `factur-x.xml` embedded |
| `InvoicePdfProcessor.SaveToPdf(…, Profile.XRechnung, …)` | `Format = "zugferd-xrechnung"` (embeds `xrechnung.xml`) |
| `InvoiceDescriptor.Load(stream or filename)` | `Parser.Parse(bytes)` (UBL, CII or hybrid PDF) |
| `InvoicePdfProcessor.LoadFromPdf(path)` | `Parser.Parse(File.ReadAllBytes(path))` |
| `InvoiceValidator.Validate(desc, version)` | `new EInvoice().Validate(bytes or path)`; official engine: `new EInvoice(new HttpValidator(apiKey: …))` |

## Reading an invoice

**ZUGFeRD-csharp**

```csharp
var desc = InvoiceDescriptor.Load("incoming.xml");
Console.WriteLine($"{desc.InvoiceNo} {desc.Seller.Name} {desc.DuePayableAmount}");
```

**WSystem.EInvoice**

```csharp
var data = Parser.Parse(File.ReadAllBytes("incoming.xml"));        // also UBL and ZUGFeRD / Factur-X PDFs
var inv = (Dictionary<string, object?>)data["invoice"]!;
var seller = (Dictionary<string, object?>)inv["seller"]!;
var totals = (Dictionary<string, object?>)inv["totals"]!;
Console.WriteLine($"{inv["invoice_number"]} {seller["name"]} {totals["amount_due"]}");
// Parser.ParseToJson(bytes) gives the same data as JSON
```

## Validating

**ZUGFeRD-csharp**

```csharp
var r = InvoiceValidator.Validate(desc, ZUGFeRDVersion.Version23);
```

**WSystem.EInvoice**

```csharp
var r = new EInvoice().Validate("invoice.xml");                   // XML Schema + KoSIT/CEN rules, offline pre-check
Console.WriteLine($"{r.Valid} [{r.Engine}, {r.Conformance}]");
foreach (var e in r.Errors) Console.WriteLine($"{e.RuleId} {e.Location}: {e.Message}");
```

`Create` validates what it generates by default (`new EInvoice(new EInvoiceOptions { NoValidation = true })` turns that
off). The offline engine is labelled `dotnet-core` / `pre-check`; for the official KoSIT / CEN engine use `HttpValidator`.

## Not in 0.9.0-beta.2 yet

If you rely on one of these, keep it on your list or open an issue:

- Embedding the XML into **your own** PDF layout (ZUGFeRD.PDF's `SaveToPdf` with a source PDF). Beta 1 renders its own invoice page.
- ZUGFeRD profiles MINIMUM, BASIC WL, BASIC and EXTENDED, and ZUGFeRD 1.0 / 2.0 output. Beta 1 writes EN 16931 (COMFORT) and XRechnung.
- Several notes or note subject codes, several bank accounts, SEPA direct-debit mandate ids, Skonto payment terms, delivery-note references, line ids you choose, gross prices and line billing periods.
- A typed result from `Parser.Parse` (it returns dictionaries).
