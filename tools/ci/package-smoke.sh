#!/usr/bin/env bash
# Fresh console app that installs WSystem.EInvoice from a local .nupkg folder (or nuget.org when no folder is given),
# creates a ZUGFeRD PDF and an XRechnung, and validates both. Usage: tools/ci/package-smoke.sh [nupkg dir] [version]
set -euo pipefail
SRC=${1:-}; VER=${2:-0.9.0-beta.2}
dir=$(mktemp -d); trap 'rm -rf "$dir"' EXIT
cd "$dir"
dotnet new console -n Smoke -o . --framework net10.0 >/dev/null
if [ -n "$SRC" ]; then
  dotnet add package WSystem.EInvoice --version "$VER" --source "$(cd "$OLDPWD" && cd "$SRC" && pwd)" >/dev/null
else
  dotnet add package WSystem.EInvoice --version "$VER" >/dev/null
fi
cat > Program.cs <<'CS'
using WSystem.EInvoice;
var inv = new Invoice
{
    Format = "zugferd", InvoiceNumber = "SMOKE-1", IssueDate = new DateTime(2026, 11, 30), DueDate = new DateTime(2026, 12, 30),
    DeliveryDate = new DateTime(2026, 11, 30), Currency = "EUR", BuyerReference = "04011000-12345-34",
    Seller = new Party { Name = "Muster GmbH", VatId = "DE123456789", Street = "Hauptstr. 1", City = "Berlin", PostalCode = "10115", Country = "DE",
        Email = "billing@muster.de", Phone = "+49 30 1234", ContactName = "Anna Muster" },
    Buyer = new Party { Name = "Kunde AG", VatId = "DE811193231", Street = "Ring 2", City = "Muenchen", PostalCode = "80331", Country = "DE", Email = "ap@kunde.de" },
    Lines = { new InvoiceLine { Description = "Consulting", Quantity = 10, Unit = "HUR", UnitPrice = 95.5m, VatRate = 19 } },
    Payment = new Payment { Iban = "DE89370400440532013000", Terms = "30 days net" },
};
var sdk = new EInvoice();
var pdf = sdk.Create(inv);
if (pdf.Valid != true || pdf.Pdf == null) { Console.Error.WriteLine("zugferd failed: " + pdf.ToJson()); return 1; }
var v = sdk.Validate(pdf.Pdf);
inv.Format = "xrechnung";
var xr = sdk.Create(inv);
Console.WriteLine($"zugferd valid={pdf.Valid} pdf={pdf.Pdf.Length}B revalidated={v.Valid} ({v.Engine}); xrechnung valid={xr.Valid}");
return v.Valid && xr.Valid == true ? 0 : 1;
CS
dotnet run -c Release
echo "package smoke ok: WSystem.EInvoice $VER"
