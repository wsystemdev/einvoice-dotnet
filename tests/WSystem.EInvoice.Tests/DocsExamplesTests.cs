using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace WSystem.EInvoice.Tests
{
    /// <summary>The code in README.md and docs/MIGRATION-FROM-ZUGFERD-CSHARP.md compiles and gives what the docs say.</summary>
    public class DocsExamplesTests
    {
        private static Invoice MigrationExample(string format) => new Invoice
        {
            Format = format,
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
            },
            Buyer = new Party
            {
                Name = "Kunde AG", Street = "Ring 2", PostalCode = "80331", City = "Muenchen", Country = "DE",
                ContactName = "Accounts payable", Email = "ap@kunde.de", VatId = "DE811193231",
            },
            Lines = { new InvoiceLine { Description = "Consulting", Quantity = 10, Unit = "HUR", UnitPrice = 95.50m, VatRate = 19 } },
            Payment = new Payment
            {
                MeansCode = "58",
                Iban = "DE89370400440532013000", Bic = "COBADEFFXXX",
                Reference = "INV-2026-001", Terms = "30 days net",
            },
        };

        [Theory]
        [InlineData("xrechnung")]
        [InlineData("xrechnung-cii")]
        [InlineData("zugferd")]
        public void MigrationGuideExample(string format)
        {
            var result = new EInvoice(new EInvoiceOptions { PdfTime = DateTimeOffset.FromUnixTimeSeconds(1764000000) }).Create(MigrationExample(format));
            Assert.True(result.Valid == true, string.Join("\n", result.Errors.ConvertAll(e => e.RuleId + ": " + e.Message)));
            Assert.Equal("1136.45", result.Totals["payable"]);
            if (format == "zugferd") Assert.NotNull(result.Pdf);

            var bytes = format == "zugferd" ? result.Pdf! : System.Text.Encoding.UTF8.GetBytes(result.Xml);
            var data = Parser.Parse(bytes);
            var inv = (Dictionary<string, object?>)data["invoice"]!;
            var seller = (Dictionary<string, object?>)inv["seller"]!;
            var totals = (Dictionary<string, object?>)inv["totals"]!;
            Assert.Equal("INV-2026-001", inv["invoice_number"]);
            Assert.Equal("Muster GmbH", seller["name"]);
            Assert.Equal("1136.45", Convert.ToString(totals["amount_due"], System.Globalization.CultureInfo.InvariantCulture));

            var path = Path.Combine(Path.GetTempPath(), "wsystem-docs-" + format + (format == "zugferd" ? ".pdf" : ".xml"));
            result.Save(path);
            var r = new EInvoice().Validate(path);
            Assert.True(r.Valid);
            Assert.Equal("dotnet-core", r.Engine);
            Assert.Equal("pre-check", r.Conformance);
            File.Delete(path);
        }

        [Fact]
        public void ReadmeExample()
        {
            var invoice = new Invoice
            {
                Format = "xrechnung", InvoiceNumber = "INV-2026-001",
                IssueDate = new DateTime(2026, 10, 18), DueDate = new DateTime(2026, 11, 17), Currency = "EUR",
                BuyerReference = "04011000-12345-34",
                Seller = new Party { Name = "Muster GmbH", VatId = "DE123456789", Street = "Hauptstr. 1", City = "Berlin",
                    PostalCode = "10115", Country = "DE", Email = "billing@muster.de", Phone = "+49 30 1234", ContactName = "Anna Muster" },
                Buyer = new Party { Name = "Kunde AG", VatId = "DE811193231", Street = "Ring 2", City = "Muenchen",
                    PostalCode = "80331", Country = "DE", Email = "ap@kunde.de" },
                Lines = { new InvoiceLine { Description = "Consulting", Quantity = 10, Unit = "HUR", UnitPrice = 95.50m, VatRate = 19 } },
                Payment = new Payment { Iban = "DE89370400440532013000", Terms = "30 days net" },
            };
            var result = new EInvoice().Create(invoice);
            Assert.True(result.Valid == true, string.Join("\n", result.Errors.ConvertAll(e => e.RuleId + ": " + e.Message)));
            Assert.Equal("1136.45", result.Totals["payable"]);
        }
    }
}
