using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WSystem.EInvoice.Validation.Core
{
    /// <summary>An expression uses XPath syntax or a function the pure-.NET engine does not implement.</summary>
    internal sealed class XPathUnsupported : Exception { public XPathUnsupported(string m) : base(m) { } }

    /// <summary>A dynamic XPath error (e.g. FORG0001 invalid cast), as Saxon would raise it.</summary>
    internal sealed class XPathError : Exception
    {
        public string XPathCode { get; }
        public XPathError(string code, string message) : base(code + ": " + message) { XPathCode = code; }
    }

    /// <summary>
    /// Parser for the XPath 2.0 subset used by the official EN 16931 / XRechnung schematron (SchXslt output).
    /// AST nodes are object[] with a string tag at [0] (same shapes as the PHP engine).
    /// </summary>
    internal sealed class XPathParser
    {
        private static readonly HashSet<string> KIND_TESTS = new HashSet<string> { "node", "text", "element", "attribute", "comment", "processing-instruction", "document-node" };
        private static readonly HashSet<string> AXES = new HashSet<string> { "child", "descendant", "attribute", "self", "descendant-or-self", "following-sibling", "following",
            "parent", "ancestor", "preceding-sibling", "preceding", "ancestor-or-self" };
        private static readonly HashSet<string> NOT_STEP = new HashSet<string> { "and", "or", "div", "mod", "idiv", "eq", "ne", "lt", "le", "gt", "ge", "to", "union", "intersect", "except", "then", "else", "return", "satisfies", "in" };

        private List<(string T, string V)> _t = new List<(string, string)>();
        private int _i;
        private static readonly ConcurrentDictionary<string, object[]> Cache = new ConcurrentDictionary<string, object[]>();

        public static object[] Parse(string expr)
        {
            if (Cache.TryGetValue(expr, out var c)) return c;
            var p = new XPathParser { _t = Lex(expr), _i = 0 };
            var ast = p.Expr();
            if (p.Peek().T != "eof") throw new XPathUnsupported($"unexpected '{p.Peek().V}' in: {expr}");
            Cache[expr] = ast;
            return ast;
        }

        private static readonly Regex NumRe = new Regex(@"\G(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant);
        private static readonly Regex NameRe = new Regex(@"\G[A-Za-z_\u0080-\uFFFF][A-Za-z0-9_.\-\u0080-\uFFFF]*(?::(?:[A-Za-z_\u0080-\uFFFF][A-Za-z0-9_.\-\u0080-\uFFFF]*|\*))?", RegexOptions.CultureInvariant);
        private static readonly Regex StarLocal = new Regex(@"\G\*:[A-Za-z_][A-Za-z0-9_.\-]*", RegexOptions.CultureInvariant);

        private static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f';
        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static List<(string, string)> Lex(string s)
        {
            var outp = new List<(string, string)>();
            int n = s.Length, i = 0;
            while (i < n)
            {
                char c = s[i];
                if (IsSpace(c)) { i++; continue; }
                if (c == '(' && i + 1 < n && s[i + 1] == ':')
                {
                    int e = s.IndexOf(":)", i + 2, StringComparison.Ordinal);
                    i = e < 0 ? n : e + 2;
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    int j = i + 1;
                    var v = new System.Text.StringBuilder();
                    while (j < n)
                    {
                        if (s[j] == c)
                        {
                            if (j + 1 < n && s[j + 1] == c) { v.Append(c); j += 2; continue; }
                            break;
                        }
                        v.Append(s[j++]);
                    }
                    outp.Add(("str", v.ToString()));
                    i = j + 1;
                    continue;
                }
                if (IsDigit(c) || (c == '.' && i + 1 < n && IsDigit(s[i + 1])))
                {
                    var m = NumRe.Match(s, i);
                    outp.Add(("num", m.Value));
                    i += m.Value.Length;
                    continue;
                }
                var nm = NameRe.Match(s, i);
                if (nm.Success)
                {
                    var w = nm.Value;
                    if (!w.EndsWith(":*", StringComparison.Ordinal) && w.Contains(":") && i + w.Length < n && s[i + w.Length] == ':')
                        w = w.Substring(0, w.IndexOf(':'));
                    outp.Add(("name", w));
                    i += w.Length;
                    continue;
                }
                bool matched = false;
                foreach (var op in new[] { "//", "::", "!=", "<=", ">=", "..", ":=" })
                {
                    if (i + 2 <= n && string.CompareOrdinal(s, i, op, 0, 2) == 0) { outp.Add(("op", op)); i += 2; matched = true; break; }
                }
                if (matched) continue;
                if ("/()[]@,|.=<>+-*$?".IndexOf(c) >= 0)
                {
                    if (c == '*' && i + 1 < n && s[i + 1] == ':')
                    {
                        var m = StarLocal.Match(s, i);
                        outp.Add(("name", m.Value));
                        i += m.Value.Length;
                        continue;
                    }
                    outp.Add(("op", c.ToString()));
                    i++;
                    continue;
                }
                throw new XPathUnsupported($"unexpected character '{c}'");
            }
            outp.Add(("eof", ""));
            return outp;
        }

        private (string T, string V) Peek(int k = 0) => _i + k < _t.Count ? _t[_i + k] : ("eof", "");
        private (string T, string V) Next() => _i < _t.Count ? _t[_i++] : ("eof", "");
        private bool IsOp(string v, int k = 0) { var t = Peek(k); return t.T == "op" && t.V == v; }
        private bool IsName(string v, int k = 0) { var t = Peek(k); return t.T == "name" && t.V == v; }
        private void Expect(string v) { var t = Next(); if (t.V != v) throw new XPathUnsupported($"expected '{v}', got '{t.V}'"); }

        private object[] Expr()
        {
            var items = new List<object> { Single() };
            while (IsOp(",")) { Next(); items.Add(Single()); }
            return items.Count == 1 ? (object[])items[0] : new object[] { "seq", items };
        }

        private object[] Single()
        {
            if ((IsName("for") || IsName("let") || IsName("some") || IsName("every")) && IsOp("$", 1))
            {
                var kw = Next().V;
                var binds = new List<object>();
                do
                {
                    if (IsOp(",")) Next();
                    Expect("$");
                    var v = Next().V;
                    if (kw == "let") Expect(":="); else Expect("in");
                    binds.Add(new object[] { v, Single() });
                } while (IsOp(","));
                if (kw == "for" || kw == "let") Expect("return"); else Expect("satisfies");
                return new object[] { kw, binds, Single() };
            }
            if (IsName("if") && IsOp("(", 1))
            {
                Next(); Expect("(");
                var c = Expr();
                Expect(")"); Expect("then");
                var a = Single();
                Expect("else");
                return new object[] { "if", c, a, Single() };
            }
            return OrExpr();
        }

        private object[] OrExpr() { var l = AndExpr(); while (IsName("or")) { Next(); l = new object[] { "or", l, AndExpr() }; } return l; }
        private object[] AndExpr() { var l = Cmp(); while (IsName("and")) { Next(); l = new object[] { "and", l, Cmp() }; } return l; }

        private object[] Cmp()
        {
            var l = Range();
            var t = Peek();
            if (t.T == "op" && (t.V == "=" || t.V == "!=" || t.V == "<" || t.V == "<=" || t.V == ">" || t.V == ">=")) { Next(); return new object[] { "gcmp", t.V, l, Range() }; }
            if (t.T == "name" && (t.V == "eq" || t.V == "ne" || t.V == "lt" || t.V == "le" || t.V == "gt" || t.V == "ge")) { Next(); return new object[] { "vcmp", t.V, l, Range() }; }
            if (t.T == "name" && t.V == "is") throw new XPathUnsupported("node comparison \"is\"");
            return l;
        }

        private object[] Range() { var l = Additive(); if (IsName("to")) { Next(); return new object[] { "to", l, Additive() }; } return l; }

        private object[] Additive()
        {
            var l = Mult();
            while (IsOp("+") || IsOp("-")) { var op = Next().V; l = new object[] { "arith", op, l, Mult() }; }
            return l;
        }

        private object[] Mult()
        {
            var l = Union();
            while (IsOp("*") || IsName("div") || IsName("idiv") || IsName("mod")) { var op = Next().V; l = new object[] { "arith", op, l, Union() }; }
            return l;
        }

        private object[] Union() { var l = Intersect(); while (IsOp("|") || IsName("union")) { Next(); l = new object[] { "union", l, Intersect() }; } return l; }

        private object[] Intersect()
        {
            var l = CastExpr();
            while (IsName("intersect") || IsName("except")) { var op = Next().V; l = new object[] { op, l, CastExpr() }; }
            return l;
        }

        private object[] CastExpr()
        {
            var l = Unary();
            foreach (var (kw, kw2) in new[] { ("instance", "of"), ("treat", "as"), ("castable", "as"), ("cast", "as") })
            {
                if (IsName(kw) && IsName(kw2, 1))
                {
                    Next(); Next();
                    var type = Next().V;
                    if (IsOp("(")) { Next(); Expect(")"); type += "()"; }
                    var occ = "";
                    if (IsOp("?") || IsOp("*") || IsOp("+")) occ = Next().V;
                    return new object[] { kw, l, type, occ };
                }
            }
            return l;
        }

        private object[] Unary()
        {
            bool neg = false;
            while (IsOp("-") || IsOp("+")) if (Next().V == "-") neg = !neg;
            var p = PathExpr();
            return neg ? new object[] { "neg", p } : p;
        }

        private static readonly object[] RootNode = { "root" };
        internal static object[] DosStep() => new object[] { "axis", "descendant-or-self", new object[] { "kind", "node" }, new List<object>() };

        private object[] PathExpr()
        {
            if (IsOp("/"))
            {
                Next();
                if (StartsStep()) return new object[] { "path", new object[] { "root" }, RelPath() };
                return new object[] { "root" };
            }
            if (IsOp("//"))
            {
                Next();
                var steps = new List<object> { DosStep() };
                steps.AddRange(RelPath());
                return new object[] { "path", new object[] { "root" }, steps };
            }
            var st = RelPath();
            return st.Count == 1 ? (object[])st[0] : new object[] { "path", null!, st };
        }

        private bool StartsStep()
        {
            var t = Peek();
            if (t.T == "name") return !NOT_STEP.Contains(t.V) || IsOp("(", 1) || IsOp("::", 1);
            return (t.T == "op" && (t.V == "@" || t.V == "." || t.V == ".." || t.V == "*" || t.V == "(" || t.V == "$")) || t.T == "str" || t.T == "num";
        }

        private List<object> RelPath()
        {
            var steps = new List<object> { Step() };
            while (IsOp("/") || IsOp("//"))
            {
                if (Next().V == "//") steps.Add(DosStep());
                steps.Add(Step());
            }
            return steps;
        }

        private object[] Step()
        {
            var t = Peek();
            if (t.T == "op" && t.V == "..") { Next(); return new object[] { "axis", "parent", new object[] { "kind", "node" }, Predicates() }; }
            if (t.T == "op" && t.V == "@") { Next(); return new object[] { "axis", "attribute", NodeTest(true), Predicates() }; }
            if (t.T == "name" && IsOp("::", 1))
            {
                if (!AXES.Contains(t.V)) throw new XPathUnsupported("axis " + t.V);
                Next(); Next();
                return new object[] { "axis", t.V, NodeTest(t.V == "attribute"), Predicates() };
            }
            if (t.T == "op" && t.V == "*") return new object[] { "axis", "child", NodeTest(false), Predicates() };
            if (t.T == "name" && (!IsOp("(", 1) || KIND_TESTS.Contains(t.V)))
            {
                if (KIND_TESTS.Contains(t.V) && IsOp("(", 1))
                    return new object[] { "axis", t.V == "attribute" ? "attribute" : "child", NodeTest(false), Predicates() };
                return new object[] { "axis", "child", NodeTest(false), Predicates() };
            }
            var prim = Primary();
            var preds = Predicates();
            return preds.Count > 0 ? new object[] { "filter", prim, preds } : prim;
        }

        private object[] NodeTest(bool attr)
        {
            var t = Next();
            if (t.T == "op" && t.V == "*") return new object[] { "any" };
            if (t.T != "name") throw new XPathUnsupported($"node test '{t.V}'");
            if (KIND_TESTS.Contains(t.V) && IsOp("("))
            {
                Next();
                string? arg = null;
                if (!IsOp(")")) arg = Next().V;
                Expect(")");
                if (t.V == "element" || t.V == "attribute")
                {
                    if (arg == null || arg == "*") return new object[] { "kind", t.V };
                    throw new XPathUnsupported("element(name)");
                }
                return new object[] { "kind", t.V };
            }
            if (t.V.StartsWith("*:", StringComparison.Ordinal)) return new object[] { "local", t.V.Substring(2) };
            if (t.V.EndsWith(":*", StringComparison.Ordinal)) return new object[] { "nsany", t.V.Substring(0, t.V.Length - 2) };
            return new object[] { "name", t.V };
        }

        private List<object> Predicates()
        {
            var p = new List<object>();
            while (IsOp("[")) { Next(); p.Add(Expr()); Expect("]"); }
            return p;
        }

        private object[] Primary()
        {
            var t = Next();
            if (t.T == "str") return new object[] { "lit", t.V };
            if (t.T == "num")
            {
                var v = t.V;
                if (v.IndexOf('e') >= 0 || v.IndexOf('E') >= 0) return new object[] { "lit", double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture) };
                if (v.Contains(".")) return new object[] { "lit", Dec.Of(v) };
                return new object[] { "lit", long.Parse(v, CultureInfo.InvariantCulture) };
            }
            if (t.T == "op" && t.V == "$") return new object[] { "var", Next().V };
            if (t.T == "op" && t.V == "(")
            {
                if (IsOp(")")) { Next(); return new object[] { "seq", new List<object>() }; }
                var e = Expr();
                Expect(")");
                return new object[] { "paren", e };
            }
            if (t.T == "op" && t.V == ".") return new object[] { "ctx" };
            if (t.T == "name" && IsOp("("))
            {
                Next();
                var args = new List<object>();
                if (!IsOp(")"))
                {
                    args.Add(Single());
                    while (IsOp(",")) { Next(); args.Add(Single()); }
                }
                Expect(")");
                return new object[] { "call", t.V, args };
            }
            throw new XPathUnsupported($"unexpected '{t.V}'");
        }
    }
}
