using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace WSystem.EInvoice.Validation
{
    /// <summary>
    /// The official rule files: the KoSIT XRechnung validator configuration (Apache-2.0), which ships the CEN EN 16931
    /// validation artefacts (EUPL-1.2) unmodified. Bundled in the assembly; no network. Pass a directory (or set
    /// WSYSTEM_EINVOICE_RULES) to use an unpacked newer KoSIT configuration instead.
    /// </summary>
    public sealed class RulePack
    {
        public const string VERSION = "2026-08-31";
        public const string SHA256 = "2530cd107c414511c5d0462ec10f886910395abfca820db82e83d70bf01221a8";
        internal const string BundledPrefix = "kosit/xrechnung-3.0.2-" + VERSION + "/";

        internal static readonly Dictionary<string, Dictionary<string, string>> XSL = new Dictionary<string, Dictionary<string, string>>
        {
            ["ubl"] = new Dictionary<string, string> { ["EN16931"] = "resources/ubl/2.1/xsl/EN16931-UBL-validation.xsl", ["XRechnung"] = "resources/xrechnung/3.0.2/xsl/XRechnung-UBL-validation.xsl" },
            ["cii"] = new Dictionary<string, string> { ["EN16931"] = "resources/cii/16b/xsl/EN16931-CII-validation.xsl", ["XRechnung"] = "resources/xrechnung/3.0.2/xsl/XRechnung-CII-validation.xsl" },
        };
        internal static readonly Dictionary<string, string> XSD = new Dictionary<string, string>
        {
            ["ubl_inv"] = "resources/ubl/2.1/xsd/maindoc/UBL-Invoice-2.1.xsd",
            ["ubl_cn"] = "resources/ubl/2.1/xsd/maindoc/UBL-CreditNote-2.1.xsd",
            ["cii"] = "resources/cii/16b/xsd/CrossIndustryInvoice_100pD16B.xsd",
        };
        public static readonly IReadOnlyDictionary<string, string> SOURCES = new Dictionary<string, string>
        {
            ["EN16931"] = "CEN/TC 434 EN 16931 validation artefacts (EUPL-1.2) https://github.com/ConnectingEurope/eInvoicing-EN16931",
            ["XRechnung"] = "KoSIT XRechnung validator configuration (Apache-2.0) https://github.com/itplr-kosit/validator-configuration-xrechnung",
        };

        private readonly string? _dir;
        private readonly ConcurrentDictionary<string, XmlSchemaSet> _schemas = new ConcurrentDictionary<string, XmlSchemaSet>();

        /// <param name="dir">unpacked KoSIT configuration directory; null = the bundled pack (or WSYSTEM_EINVOICE_RULES when set)</param>
        public RulePack(string? dir = null)
        {
            var env = Environment.GetEnvironmentVariable("WSYSTEM_EINVOICE_RULES");
            _dir = dir ?? (string.IsNullOrEmpty(env) ? null : env);
        }

        public bool IsBundled => _dir == null;
        public string Dir => _dir ?? "(bundled) " + BundledPrefix;

        internal string Key(string rel) => (_dir ?? "bundled:") + "|" + rel;

        internal static Dictionary<string, string> RuleVersions() => new Dictionary<string, string>
        {
            ["EN16931"] = "1.3.15+ via KoSIT config", ["XRechnung"] = "3.0.2 config " + VERSION,
        };

        // ---------------------------------------------------------------- resources
        private static readonly Assembly Asm = typeof(RulePack).Assembly;
        private static readonly Lazy<Dictionary<string, string>> ResNames = new Lazy<Dictionary<string, string>>(() =>
            Asm.GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.Ordinal));

        /// <summary>Opens a bundled resource by its path below Resources/ (e.g. "fonts/Vera.json").</summary>
        internal static Stream OpenResource(string rel)
        {
            if (!ResNames.Value.TryGetValue("WSystem.EInvoice.Resources/" + rel, out var name))
                throw new FileNotFoundException("bundled resource missing: " + rel);
            return Asm.GetManifestResourceStream(name)!;
        }

        internal static byte[] ReadResource(string rel)
        {
            using var s = OpenResource(rel);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        internal Stream Open(string rel)
        {
            if (_dir != null)
            {
                var p = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(p)) throw new FileNotFoundException($"rule file missing: {p}");
                return File.OpenRead(p);
            }
            return OpenResource(BundledPrefix + rel);
        }

        internal XmlDocument LoadXml(string rel)
        {
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            using var s = Open(rel);
            using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc.Load(r);
            return doc;
        }

        internal string XslPath(string syntax, string ruleset) => XSL[syntax][ruleset];

        /// <summary>Compiled XML Schema for a document type (cached).</summary>
        internal XmlSchemaSet Schema(string documentType) => _schemas.GetOrAdd(documentType, dt =>
        {
            var resolver = new PackResolver(this);
            var set = new XmlSchemaSet { XmlResolver = resolver };
            var uri = new Uri(PackResolver.Base, XSD[dt]);
            using (var reader = XmlReader.Create(Open(XSD[dt]), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = resolver }, uri.AbsoluteUri))
                set.Add(null, reader);
            set.Compile();
            return set;
        });

        /// <summary>Resolves relative schema imports inside the pack (never touches the network).</summary>
        private sealed class PackResolver : XmlResolver
        {
            internal static readonly Uri Base = new Uri("file:///wsystem-rule-pack/");
            private readonly RulePack _pack;
            public PackResolver(RulePack p) { _pack = p; }
            public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
            {
                if (absoluteUri.Scheme != "file" || !absoluteUri.AbsolutePath.StartsWith(Base.AbsolutePath, StringComparison.Ordinal))
                    throw new XmlException("external resource not allowed: " + absoluteUri);
                var rel = Uri.UnescapeDataString(absoluteUri.AbsolutePath.Substring(Base.AbsolutePath.Length));
                return _pack.Open(rel);
            }
#if NET8_0_OR_GREATER
            public override System.Net.ICredentials Credentials { set { } }
#else
            public override System.Net.ICredentials? Credentials { set { } }
#endif
        }
    }
}
