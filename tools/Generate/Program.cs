// Generate every fixture with the .NET SDK into <outdir> (XML, plus PDF for the ZUGFeRD/Factur-X formats) and write
// <outdir>/manifest.json: name => {format, xrechnung, xml, pdf, expected_valid}. PDF dates fixed for reproducible files.
// Used by the CI "official-validators" job (KoSIT, veraPDF, Mustang).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using WSystem.EInvoice;

var root = args.Length > 1 ? args[1] : FindRoot();
var outDir = args.Length > 0 ? args[0] : "build/official";
Directory.CreateDirectory(outDir);
var fx = Path.Combine(root, "tests", "WSystem.EInvoice.Tests", "fixtures");
var inputs = JsonNode.Parse(File.ReadAllText(Path.Combine(fx, "inputs.json")))!.AsArray();
var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(fx, "expected.json")))!.AsObject();
var sdk = new EInvoice(new EInvoiceOptions { NoValidation = true, PdfTime = DateTimeOffset.FromUnixTimeSeconds(1764000000) });
var man = new JsonObject();
int pdfs = 0;
foreach (var f in inputs)
{
    var name = (string)f!["name"]!;
    CreateResult r;
    try { r = sdk.Create(f["input"]!.ToJsonString()); }
    catch (InputError) { continue; }
    File.WriteAllText(Path.Combine(outDir, name + ".xml"), r.Xml);
    var xr = r.Format is "xrechnung" or "xrechnung-cii" or "zugferd-xrechnung";
    var e = new JsonObject { ["format"] = r.Format, ["xrechnung"] = xr, ["xml"] = name + ".xml", ["pdf"] = null,
        ["expected_valid"] = expected[name]?["valid"]?.DeepClone() };
    if (r.Pdf != null)
    {
        var back = WSystem.EInvoice.Pdf.AttachmentReader.Read(r.Pdf);
        if (!back.TryGetValue(r.EmbeddedFile!, out var x) || System.Text.Encoding.UTF8.GetString(x) != r.Xml)
        {
            Console.Error.WriteLine("ROUND TRIP FAILED " + name);
            return 1;
        }
        File.WriteAllBytes(Path.Combine(outDir, name + ".pdf"), r.Pdf);
        e["pdf"] = name + ".pdf";
        pdfs++;
    }
    man[name] = e;
}
File.WriteAllText(Path.Combine(outDir, "manifest.json"), man.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{man.Count} fixtures, {pdfs} PDFs written to {outDir}");
return 0;

static string FindRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "WSystem.EInvoice.sln"))) d = d.Parent;
    return d?.FullName ?? Directory.GetCurrentDirectory();
}
