using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WSystem.EInvoice.Support;
using Xunit;

namespace WSystem.EInvoice.Tests
{
    public class EInvoiceTests
    {
        private static readonly Dictionary<string, object?> Inputs = ((List<object?>)Json.Decode(File.ReadAllText(ParityTests.Fx("inputs.json")))!)
            .ToDictionary(f => Py.Str(Py.G(f, "name")), f => Py.G(f, "input"));
        private static readonly object? Expected = Json.Decode(File.ReadAllText(ParityTests.Fx("expected.json")));
        private static readonly EInvoice Sdk = new EInvoice(new EInvoiceOptions { PdfTime = PdfTests.T });

        public static IEnumerable<object[]> Cases() => ((Dictionary<string, object?>)Json.Decode(File.ReadAllText(ParityTests.Fx("expected.json")))!)
            .Where(k => !Py.Has(k.Value, "error")).Select(k => new object[] { k.Key });

        [Theory]
        [MemberData(nameof(Cases))]
        public void CreateMatchesApi(string name)
        {
            var exp = Py.G(Expected, name);
            var r = Sdk.Create(Json.Encode(Inputs[name]));
            Assert.Equal(Py.Str(Py.G(exp, "xml")), r.Xml);
            Assert.Equal((bool)Py.G(exp, "valid")!, r.Valid);
            Assert.Equal("dotnet-core", r.Engine);
            Assert.Equal("pre-check", r.Conformance);
            if (r.EmbeddedFile != null)
            {
                Assert.Equal(r.Valid == true, r.Pdf != null);
                if (r.Pdf != null)
                {
                    Assert.Equal(r.Pdf.Length, r.PdfInfo!.Bytes);
                    Assert.EndsWith(".pdf", r.PdfFilename);
                }
            }
        }

        [Fact]
        public void TypedInvoiceGivesSameXmlAsJson()
        {
            var inv = new Invoice
            {
                Format = "en16931", InvoiceNumber = "INV-2026-001", IssueDate = new DateTime(2026, 10, 18), DueDate = new DateTime(2026, 11, 17),
                DeliveryDate = new DateTime(2026, 10, 18), Currency = "EUR", BuyerReference = "04011000-12345-34",
                Seller = new Party { Name = "Muster GmbH", VatId = "DE123456789", Street = "Hauptstr. 1", City = "Berlin", PostalCode = "10115", Country = "DE",
                    Email = "billing@muster.de", Phone = "+49 30 1234", ContactName = "Anna Muster" },
                Buyer = new Party { Name = "Kunde AG", VatId = "DE811193231", Street = "Ring 2", City = "Muenchen", PostalCode = "80331", Country = "DE", Email = "ap@kunde.de" },
                Lines =
                {
                    new InvoiceLine { Description = "Consulting", Quantity = 10, Unit = "HUR", UnitPrice = 95.5m, VatRate = 19 },
                    new InvoiceLine { Description = "Book", Quantity = 2.0m, UnitPrice = 12.990m, VatRate = 7 },
                },
                Payment = new Payment { Iban = "DE89370400440532013000", Terms = "30 days net" },
            };
            var first = ((List<object?>)Json.Decode(File.ReadAllText(ParityTests.Fx("inputs.json")))!)[0];
            Assert.Equal(EInvoice.Xml(Json.Encode(Py.G(first, "input"))), EInvoice.Xml(inv));
            var r = new EInvoice().Create(inv);
            Assert.True(r.Valid);
            Assert.Equal("1164.25", r.Totals["payable"]);
        }

        [Fact]
        public void ZugferdCreatesValidPdfA3()
        {
            var r = Sdk.Create(Json.Encode(Inputs["sample-zugferd"]));
            Assert.True(r.Valid, string.Join(", ", r.Errors.Select(e => e.RuleId)));
            Assert.NotNull(r.Pdf);
            Assert.Equal("factur-x.xml", r.EmbeddedFile);
            Assert.Equal("EN 16931 (COMFORT)", r.ZugferdProfile);
            var v = Sdk.Validate(r.Pdf!);
            Assert.True(v.Valid);
            Assert.Equal("pdf", v.Container);
            Assert.Equal(r.Pdf!.Length, r.PdfInfo!.Bytes);
            Assert.Contains("\"pdf\":\"JVBERi0", r.ToJson());
        }

        [Fact]
        public void InvalidInputThrowsInputError()
        {
            Assert.Throws<InputError>(() => Sdk.Create("{\"format\":\"nope\",\"lines\":[]}"));
            Assert.Throws<InputError>(() => Sdk.Validate(new byte[0]));
            Assert.Throws<InputError>(() => Sdk.Validate(System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a 'b'>]><x/>")));
        }

        [Fact]
        public void NoValidationAlwaysBuildsPdf()
        {
            var r = new EInvoice(new EInvoiceOptions { NoValidation = true }).Create(Json.Encode(Inputs["sample-zugferd"]));
            Assert.Null(r.Valid);
            Assert.Equal("none", r.Engine);
            Assert.NotNull(r.Pdf);
        }
    }

    public class HttpValidatorTests
    {
        [Fact]
        public void InterpretsApiAnswer()
        {
            var body = "{\"valid\":true,\"error_count\":0,\"warning_count\":0,\"container\":\"xml\",\"syntax\":\"ubl\",\"document_type\":\"ubl_inv\",\"checked\":{\"xsd\":true,\"EN16931\":true,\"XRechnung\":true,\"PeppolBIS3\":false},\"errors\":[],\"warnings\":[],\"engine\":\"saxon\"}";
            var r = Validation.HttpValidator.Interpret(200, body, "https://api.wsystem.dev/einvoice/validate?rules=auto", "https://api.wsystem.dev");
            Assert.True(r.Valid);
            Assert.Equal("remote", r.Engine);
            Assert.Equal("official", r.Conformance);
            Assert.Equal(true, r.Checked["EN16931"]);
            Assert.Equal("https://api.wsystem.dev", r.RemoteUrl);
        }

        [Fact]
        public void Http400IsInputError()
        {
            var e = Assert.Throws<InputError>(() => Validation.HttpValidator.Interpret(400, "{\"message\":\"not well-formed XML\"}", "u", "b"));
            Assert.Equal("not well-formed XML", e.Message);
            Assert.Throws<InvalidOperationException>(() => Validation.HttpValidator.Interpret(500, "oops", "u", "b"));
        }

        /// <summary>Opt-in round trip with the live W System API (sends a sample invoice): WSYSTEM_API_ROUNDTRIP=1.</summary>
        [Fact]
        public void RoundTripWithApi()
        {
            if (Environment.GetEnvironmentVariable("WSYSTEM_API_ROUNDTRIP") != "1") return;
            var url = Environment.GetEnvironmentVariable("WSYSTEM_API_URL") ?? Validation.HttpValidator.DefaultBaseUrl;
            var remote = new Validation.HttpValidator(url, Environment.GetEnvironmentVariable("WSYSTEM_API_KEY"));
            var expected = Json.Decode(File.ReadAllText(ParityTests.Fx("expected.json")));
            foreach (var name in new[] { "sample-xrechnung", "sample-zugferd", "sample-cn-xrechnung" })
            {
                if (!Py.Has(expected, name)) continue;
                var inputs = ((List<object?>)Json.Decode(File.ReadAllText(ParityTests.Fx("inputs.json")))!).First(f => Py.Str(Py.G(f, "name")) == name);
                var created = new EInvoice(new EInvoiceOptions { NoValidation = true }).Create(Json.Encode(Py.G(inputs, "input")));
                var data = created.Pdf ?? System.Text.Encoding.UTF8.GetBytes(created.Xml);
                var local = new Validation.Validator().Validate(data);
                var r = remote.Validate(data);
                Assert.Equal("remote", r.Engine);
                Assert.Equal(r.Valid, local.Valid);
                Assert.Equal(r.Errors.Select(x => x.RuleId).OrderBy(x => x), local.Errors.Select(x => x.RuleId).OrderBy(x => x));
            }
        }
    }
}
