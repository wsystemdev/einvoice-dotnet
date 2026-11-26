using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace WSystem.EInvoice.Validation.Core
{
    internal sealed class SchAssert
    {
        public string Id = "", Flag = "fatal", Test = "";
        public List<object> Text = new List<object>(); // string | TextSelect
        public object[]? Ast;
    }

    internal sealed class TextSelect { public string Select = ""; public object[]? Ast; }

    internal sealed class SchRule
    {
        public string Match = "", Pattern = "";
        public double Priority;
        public List<SchAssert> Asserts = new List<SchAssert>();
        public List<(string Name, string Select, object[]? Ast)> Vars = new List<(string, string, object[]?)>();
        public object[]? Ast;
    }

    internal sealed class Fail { public string Id = "", Flag = "", Text = "", Location = ""; }
    internal sealed class EvalProblem { public string Id = "", Error = ""; public string? Location; }

    /// <summary>
    /// Reads an official SchXslt-compiled schematron XSLT (the bundled CEN / KoSIT files, unmodified) and runs its rules
    /// with the pure-.NET XPath engine, reproducing SchXslt's semantics: per pattern, the first matching rule (highest
    /// priority) fires for a node and later ones are suppressed; nodes are visited in document order, attributes before
    /// children; the report lists patterns in declaration order.
    /// </summary>
    internal sealed class SchematronProgram
    {
        private const string XSL = "http://www.w3.org/1999/XSL/Transform";
        private const string SVRL = "http://purl.oclc.org/dsdl/svrl";
        private const string SCHXSLT = "https://doi.org/10.5281/zenodo.1495494";
        private static readonly ConcurrentDictionary<string, SchematronProgram> Loaded = new ConcurrentDictionary<string, SchematronProgram>();

        public readonly Dictionary<string, string> Ns = new Dictionary<string, string>();
        private readonly List<(string Name, string? Select, string Text)> _globals = new List<(string, string?, string)>();
        private readonly Dictionary<string, (List<(string, string?)> Params, List<(string, string)> Vars, string? Body, string? Unsupported)> _functionDefs
            = new Dictionary<string, (List<(string, string?)>, List<(string, string)>, string?, string?)>();
        public readonly List<string> Patterns = new List<string>();
        public readonly List<SchRule> Rules = new List<SchRule>();
        public readonly Dictionary<string, string> Unsupported = new Dictionary<string, string>();

        public static SchematronProgram Load(string key, Func<XmlDocument> loader) => Loaded.GetOrAdd(key, _ => new SchematronProgram(loader()));

        private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

        private SchematronProgram(XmlDocument dom)
        {
            var root = dom.DocumentElement!;
            foreach (XmlAttribute a in root.Attributes)
                if (a.Prefix == "xmlns" && a.LocalName != "xml") Ns[a.LocalName] = a.Value;
            var nsm = new XmlNamespaceManager(dom.NameTable);
            nsm.AddNamespace("xsl", XSL);
            nsm.AddNamespace("svrl", SVRL);
            nsm.AddNamespace("schxslt", SCHXSLT);
            foreach (XmlElement v in dom.SelectNodes("/xsl:transform/xsl:param | /xsl:transform/xsl:variable", nsm)!)
                _globals.Add((v.GetAttribute("name"), v.HasAttribute("select") ? v.GetAttribute("select") : null, v.InnerText));
            foreach (XmlElement f in dom.SelectNodes("/xsl:transform/xsl:function", nsm)!)
            {
                var name = f.GetAttribute("name");
                if (name == "schxslt:location") continue;
                var ps = new List<(string, string?)>(); var vs = new List<(string, string)>(); string? body = null, unsup = null;
                foreach (XmlNode ch0 in f.ChildNodes)
                {
                    if (!(ch0 is XmlElement ch) || ch.NamespaceURI != XSL) continue;
                    switch (ch.LocalName)
                    {
                        case "param": ps.Add((ch.GetAttribute("name"), ch.HasAttribute("as") ? ch.GetAttribute("as") : null)); break;
                        case "variable": vs.Add((ch.GetAttribute("name"), ch.HasAttribute("select") ? ch.GetAttribute("select") : Quote(ch.InnerText))); break;
                        case "sequence": body = ch.GetAttribute("select"); break;
                        default: unsup = ch.LocalName; break;
                    }
                }
                _functionDefs[name] = (ps, vs, body, unsup);
            }
            var modes = new List<string>();
            foreach (XmlElement t in dom.SelectNodes("/xsl:transform/xsl:template[@name][.//schxslt:pattern]", nsm)!)
            {
                foreach (XmlElement p in t.SelectNodes(".//schxslt:pattern", nsm)!) Patterns.Add(p.GetAttribute("id"));
                foreach (XmlElement a in t.SelectNodes(".//xsl:apply-templates[@mode]", nsm)!) modes.Add(a.GetAttribute("mode"));
            }
            foreach (var mode in modes)
            {
                var list = new List<SchRule>();
                foreach (XmlElement t in dom.SelectNodes($"/xsl:transform/xsl:template[@mode='{mode}'][@match]", nsm)!)
                {
                    if (!(t.SelectSingleNode("xsl:choose/xsl:otherwise/schxslt:rule", nsm) is XmlElement rule)) continue;
                    var r = new SchRule
                    {
                        Match = t.GetAttribute("match"),
                        Priority = double.TryParse(t.GetAttribute("priority"), NumberStyles.Float, CultureInfo.InvariantCulture, out var pr) ? pr : 0,
                        Pattern = rule.GetAttribute("pattern"),
                    };
                    foreach (XmlElement v in t.SelectNodes("xsl:variable", nsm)!)
                        r.Vars.Add((v.GetAttribute("name"), v.HasAttribute("select") ? v.GetAttribute("select") : Quote(v.InnerText), null));
                    foreach (XmlElement iff in rule.SelectNodes("xsl:if[svrl:failed-assert or svrl:successful-report]", nsm)!)
                    {
                        var fa = (XmlElement)iff.SelectSingleNode("svrl:failed-assert | svrl:successful-report", nsm)!;
                        var a = new SchAssert { Id = fa.GetAttribute("id"), Test = iff.GetAttribute("test") };
                        var flag = fa.GetAttribute("flag");
                        if (flag.Length == 0) flag = fa.GetAttribute("role");
                        a.Flag = flag.Length == 0 ? "fatal" : flag;
                        if (fa.SelectSingleNode("svrl:text", nsm) is XmlElement textEl)
                        {
                            foreach (XmlNode tn in textEl.ChildNodes)
                            {
                                if (tn is XmlText || tn is XmlWhitespace || tn is XmlSignificantWhitespace || tn is XmlCDataSection) a.Text.Add(((XmlCharacterData)tn).Data);
                                else if (tn is XmlElement te && te.NamespaceURI == XSL && te.LocalName == "value-of") a.Text.Add(new TextSelect { Select = te.GetAttribute("select") });
                            }
                        }
                        r.Asserts.Add(a);
                    }
                    list.Add(r);
                }
                Rules.AddRange(list.OrderByDescending(x => x.Priority));
            }
            Compile();
        }

        private void Compile()
        {
            foreach (var r in Rules)
            {
                try
                {
                    r.Ast = Anchor(XPathParser.Parse(r.Match));
                    for (int i = 0; i < r.Vars.Count; i++) r.Vars[i] = (r.Vars[i].Name, r.Vars[i].Select, XPathParser.Parse(r.Vars[i].Select));
                }
                catch (XPathUnsupported e)
                {
                    r.Ast = null;
                    foreach (var a in r.Asserts) Unsupported[a.Id] = "context: " + e.Message;
                    continue;
                }
                foreach (var a in r.Asserts)
                {
                    try
                    {
                        a.Ast = XPathParser.Parse(a.Test);
                        foreach (var p in a.Text) if (p is TextSelect ts) ts.Ast = XPathParser.Parse(ts.Select);
                    }
                    catch (XPathUnsupported e)
                    {
                        a.Ast = null;
                        Unsupported[a.Id] = e.Message;
                    }
                }
            }
        }

        private static object[] Anchor(object[] p)
        {
            if (Absolute(p)) return p;
            var tag = (string)p[0];
            if (tag == "union") return new object[] { "union", Anchor((object[])p[1]), Anchor((object[])p[2]) };
            if (tag == "root" || (tag == "path" && p[1] != null)) return p;
            if (tag == "call" && (string)p[1] == "root") return new object[] { "root" };
            var dos = XPathParser.DosStep();
            if (tag == "axis") return new object[] { "path", new object[] { "root" }, new List<object> { dos, p } };
            if (tag == "path" && p[1] == null && (string)((object[])((List<object>)p[2])[0])[0] == "axis")
            {
                var steps = new List<object> { dos };
                steps.AddRange((List<object>)p[2]);
                return new object[] { "path", new object[] { "root" }, steps };
            }
            throw new XPathUnsupported("match pattern");
        }

        private static bool Absolute(object[] p)
        {
            switch ((string)p[0])
            {
                case "root": return true;
                case "union": return Absolute((object[])p[1]) && Absolute((object[])p[2]);
                case "paren": return Absolute((object[])p[1]);
                case "filter": return Absolute((object[])p[1]);
                case "path":
                    if (p[1] != null) return Absolute((object[])p[1]);
                    var first = (object[])((List<object>)p[2])[0];
                    return (string)first[0] != "axis" && Absolute(first);
                case "call": return (string)p[1] == "root";
                default: return false;
            }
        }

        public List<string> RuleIds()
        {
            var ids = new List<string>(); var seen = new HashSet<string>();
            foreach (var r in Rules) foreach (var a in r.Asserts) if (seen.Add(a.Id)) ids.Add(a.Id);
            return ids;
        }

        private static bool IsEngineFault(Exception e) => e is XPathUnsupported || e is ArithmeticException || e is InvalidCastException || e is ArgumentException || e is NullReferenceException || e is IndexOutOfRangeException || e is KeyNotFoundException || e is FormatException;

        public (List<Fail> Fails, List<EvalProblem> Errors) Run(Tree tree)
        {
            var ev = new XPathEvaluator(tree) { Ns = Ns };
            foreach (var (name, select, text) in _globals)
            {
                if (select == null) ev.LazyGlobals[name] = () => new List<object> { text };
                else ev.LazyGlobals[name] = () => ev.Evaluate(XPathParser.Parse(select), tree.Root);
            }
            foreach (var kv in _functionDefs)
            {
                var def = kv.Value;
                if (def.Unsupported != null || def.Body == null) continue;
                try
                {
                    ev.Functions[kv.Key] = new UserFunction
                    {
                        Params = def.Params,
                        Vars = def.Vars.Select(v => (v.Item1, XPathParser.Parse(v.Item2))).ToList(),
                        Body = XPathParser.Parse(def.Body),
                    };
                }
                catch (XPathUnsupported) { }
            }
            var fired = new Dictionary<string, HashSet<int>>();
            var byPattern = new Dictionary<string, List<Fail>>();
            foreach (var p in Patterns) if (!byPattern.ContainsKey(p)) byPattern[p] = new List<Fail>();
            var failures = new List<EvalProblem>();
            var matchSets = new List<(int Index, HashSet<int> Set)>();
            for (int i = 0; i < Rules.Count; i++)
            {
                var r = Rules[i];
                if (r.Ast == null) continue;
                try
                {
                    var set = new HashSet<int>();
                    ev.PatternMode = true;
                    foreach (var n in ev.Evaluate(r.Ast, tree.Root)) set.Add(((Node)n).Id);
                    matchSets.Add((i, set));
                }
                catch (XPathError) { throw; }
                catch (Exception e) when (IsEngineFault(e))
                {
                    foreach (var a in r.Asserts) failures.Add(new EvalProblem { Id = a.Id, Error = "context: " + e.Message });
                }
                finally { ev.PatternMode = false; }
            }
            var errors = new List<EvalProblem>();
            foreach (var node in tree.All)
            {
                foreach (var (i, set) in matchSets)
                {
                    if (!set.Contains(node.Id)) continue;
                    var r = Rules[i];
                    if (!fired.TryGetValue(r.Pattern, out var fs)) fired[r.Pattern] = fs = new HashSet<int>();
                    if (!fs.Add(node.Id)) continue;
                    ev.Current = node;
                    var vars = new Dictionary<string, List<object>>();
                    try
                    {
                        foreach (var v in r.Vars) vars[v.Name] = ev.Evaluate(v.Ast!, node, vars);
                    }
                    catch (XPathError) { throw; }
                    catch (Exception e) when (IsEngineFault(e))
                    {
                        foreach (var a in r.Asserts) errors.Add(new EvalProblem { Id = a.Id, Error = "rule variable: " + e.Message, Location = Location(node) });
                        continue;
                    }
                    foreach (var a in r.Asserts)
                    {
                        if (a.Ast == null) continue;
                        try
                        {
                            if (!ev.EbvOf(a.Ast, node, vars)) continue;
                            var text = new StringBuilder();
                            foreach (var p in a.Text)
                                text.Append(p is TextSelect ts ? string.Join(" ", ev.Atomize(ev.Evaluate(ts.Ast!, node, vars)).Select(ev.Str)) : (string)p);
                            if (!byPattern.TryGetValue(r.Pattern, out var bl)) byPattern[r.Pattern] = bl = new List<Fail>();
                            bl.Add(new Fail { Id = a.Id, Flag = a.Flag, Text = text.ToString(), Location = Location(node) });
                        }
                        catch (XPathError e)
                        {
                            throw new XPathError(e.XPathCode, e.Message.Substring(e.XPathCode.Length + 2) + $" (rule {a.Id} at " + Location(node) + ")");
                        }
                        catch (Exception e) when (IsEngineFault(e))
                        {
                            errors.Add(new EvalProblem { Id = a.Id, Error = e.Message, Location = Location(node) });
                        }
                    }
                }
            }
            var outp = new List<Fail>();
            foreach (var list in byPattern.Values) outp.AddRange(list);
            failures.AddRange(errors);
            return (outp, failures);
        }

        /// <summary>schxslt:location(): /Q{ns}local[n]/... with @Q{ns}local for attributes</summary>
        public static string Location(Node n)
        {
            var seg = new List<string>();
            for (var x = n; x != null && x.Kind != Node.DOC; x = x.Parent)
            {
                if (x.Kind == Node.ATTR) { seg.Add("@Q{" + x.Ns + "}" + x.Local); continue; }
                int pos = 1;
                foreach (var s in x.Parent!.Children)
                {
                    if (ReferenceEquals(s, x)) break;
                    if (s.Kind == x.Kind && (x.Kind != Node.ELEM || (s.Local == x.Local && s.Ns == x.Ns))) pos++;
                }
                switch (x.Kind)
                {
                    case Node.ELEM: seg.Add("Q{" + x.Ns + "}" + x.Local + "[" + pos + "]"); break;
                    case Node.TEXT: seg.Add("text()[" + pos + "]"); break;
                    case Node.COMMENT: seg.Add("comment()[" + pos + "]"); break;
                    default: seg.Add("processing-instruction(\"" + x.Local + "\")[" + pos + "]"); break;
                }
            }
            seg.Reverse();
            return "/" + string.Join("/", seg);
        }
    }
}
