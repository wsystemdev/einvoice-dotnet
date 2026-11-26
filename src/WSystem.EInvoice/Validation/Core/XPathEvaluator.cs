using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WSystem.EInvoice.Validation.Core
{
    internal sealed class Ctx
    {
        public readonly object? Item; public readonly int Pos, Size; public readonly Dictionary<string, List<object>> Vars;
        public Ctx(object? item, int pos, int size, Dictionary<string, List<object>> vars) { Item = item; Pos = pos; Size = size; Vars = vars; }
    }

    internal sealed class UserFunction
    {
        public List<(string Name, string? Type)> Params = new List<(string, string?)>();
        public List<(string Name, object[] Ast)> Vars = new List<(string, object[])>();
        public object[] Body = Array.Empty<object>();
    }

    /// <summary>
    /// Evaluates XPathParser ASTs over a Tree with XPath 2.0 semantics. Items: Node, string (xs:string), Untyped,
    /// long (xs:integer), Dec (xs:decimal), double (xs:double), bool, DateV. Port of the PHP SDK's php-core engine.
    /// </summary>
    internal sealed class XPathEvaluator
    {
        public Dictionary<string, string> Ns = new Dictionary<string, string>();
        public Dictionary<string, List<object>> Globals = new Dictionary<string, List<object>>();
        public Dictionary<string, Func<List<object>>> LazyGlobals = new Dictionary<string, Func<List<object>>>();
        public Dictionary<string, UserFunction> Functions = new Dictionary<string, UserFunction>();
        public Node? Current;
        public bool PatternMode;
        public readonly Tree Tree;

        private static readonly Dictionary<string, List<object>> NoVars = new Dictionary<string, List<object>>();

        public XPathEvaluator(Tree tree) { Tree = tree; }

        public List<object> Evaluate(object[] ast, object? ctx, Dictionary<string, List<object>>? vars = null, int pos = 1, int size = 1)
            => Ev(ast, new Ctx(ctx, pos, size, vars ?? NoVars));

        public bool EbvOf(object[] ast, object? ctx, Dictionary<string, List<object>>? vars = null)
            => Ebv(Ev(ast, new Ctx(ctx, 1, 1, vars ?? NoVars)));

        private static List<object> L(params object[] x) => new List<object>(x);
        private static Dictionary<string, List<object>> With(Dictionary<string, List<object>> vars, string name, List<object> v)
        {
            var d = new Dictionary<string, List<object>>(vars) { [name] = v };
            return d;
        }

        private static readonly Dictionary<string, string> VOp = new Dictionary<string, string> { ["eq"] = "=", ["ne"] = "!=", ["lt"] = "<", ["le"] = "<=", ["gt"] = ">", ["ge"] = ">=" };

        private List<object> Ev(object[] a, Ctx c)
        {
            switch ((string)a[0])
            {
                case "lit": return L(a[1]);
                case "seq":
                    {
                        var outp = new List<object>();
                        foreach (var x in (List<object>)a[1]) outp.AddRange(Ev((object[])x, c));
                        return outp;
                    }
                case "paren": return Ev((object[])a[1], c);
                case "ctx":
                    if (c.Item == null) throw new XPathError("XPDY0002", "no context item");
                    return L(c.Item);
                case "root": return L(Tree.Root);
                case "var": return Variable((string)a[1], c);
                case "or":
                case "and":
                    {
                        bool decide = (string)a[0] == "or";
                        bool left;
                        try { left = Ebv(Ev((object[])a[1], c)); }
                        catch (XPathError)
                        {
                            if (Ebv(Ev((object[])a[2], c)) == decide) return L(decide);
                            throw;
                        }
                        return L(left == decide ? decide : Ebv(Ev((object[])a[2], c)));
                    }
                case "if": return Ebv(Ev((object[])a[1], c)) ? Ev((object[])a[2], c) : Ev((object[])a[3], c);
                case "for": case "let": case "some": case "every":
                    return Flwor(a, c, 0, c.Vars);
                case "gcmp":
                    {
                        var (l, r) = Operands((object[])a[2], (object[])a[3], c);
                        return l == null ? L(false) : L(GeneralCompare((string)a[1], l, r!));
                    }
                case "vcmp":
                    {
                        var l = Atomize(Ev((object[])a[2], c));
                        var r = Atomize(Ev((object[])a[3], c));
                        if (l.Count == 0 || r.Count == 0) return new List<object>();
                        if (l.Count > 1 || r.Count > 1) throw new XPathError("XPTY0004", "value comparison of a sequence");
                        object lv = l[0] is Untyped ul ? ul.V : l[0];
                        object rv = r[0] is Untyped ur ? ur.V : r[0];
                        return L(CompareAtoms(VOp[(string)a[1]], lv, rv));
                    }
                case "to":
                    {
                        var l = Atomize(Ev((object[])a[1], c));
                        var r = Atomize(Ev((object[])a[2], c));
                        if (l.Count == 0 || r.Count == 0) return new List<object>();
                        long from = ToInt(l[0]), to = ToInt(r[0]);
                        var outp = new List<object>();
                        for (long i = from; i <= to; i++) outp.Add(i);
                        return outp;
                    }
                case "arith":
                    {
                        var (l, r) = Operands((object[])a[2], (object[])a[3], c);
                        return l == null ? new List<object>() : Arith((string)a[1], l, r!);
                    }
                case "neg":
                    {
                        var v = Atomize(Ev((object[])a[1], c));
                        if (v.Count == 0) return new List<object>();
                        object x = v[0] is Untyped u ? CastDouble(u) : v[0];
                        if (x is double d) return L(-d);
                        return Arith("-", L(0L), L(x));
                    }
                case "union":
                    {
                        var all = NodesOnly(Ev((object[])a[1], c));
                        all.AddRange(NodesOnly(Ev((object[])a[2], c)));
                        return DocOrder(all);
                    }
                case "intersect":
                case "except":
                    {
                        var l = NodesOnly(Ev((object[])a[1], c));
                        var r = new HashSet<int>();
                        foreach (var n in NodesOnly(Ev((object[])a[2], c))) r.Add(((Node)n).Id);
                        bool inter = (string)a[0] == "intersect";
                        return DocOrder(l.Where(n => inter == r.Contains(((Node)n).Id)).ToList());
                    }
                case "instance": return L(InstanceOf(Ev((object[])a[1], c), (string)a[2], (string)a[3]));
                case "castable":
                    {
                        var v = Atomize(Ev((object[])a[1], c));
                        try
                        {
                            if (v.Count == 0) return L((string)a[3] == "?");
                            if (v.Count > 1) return L(false);
                            Cast(v[0], (string)a[2]);
                            return L(true);
                        }
                        catch (XPathError) { return L(false); }
                    }
                case "cast":
                    {
                        var v = Atomize(Ev((object[])a[1], c));
                        if (v.Count == 0)
                        {
                            if ((string)a[3] == "?") return new List<object>();
                            throw new XPathError("XPTY0004", "cast of empty sequence");
                        }
                        return L(Cast(v[0], (string)a[2]));
                    }
                case "treat": return Ev((object[])a[1], c);
                case "call": return Call((string)a[1], (List<object>)a[2], c);
                case "filter":
                    {
                        var seq = Ev((object[])a[1], c);
                        foreach (var p in (List<object>)a[2]) seq = ApplyPredicate(seq, (object[])p, c);
                        return seq;
                    }
                case "axis":
                    if (!(c.Item is Node cn)) throw new XPathError("XPTY0020", "axis step on a non-node");
                    return AxisStep(cn, a, c);
                case "path": return PathEv(a, c);
            }
            throw new XPathUnsupported("expression " + a[0]);
        }

        private List<object> Variable(string name, Ctx c)
        {
            if (c.Vars.TryGetValue(name, out var v)) return v;
            if (Globals.TryGetValue(name, out var g)) return g;
            if (LazyGlobals.TryGetValue(name, out var f))
            {
                LazyGlobals.Remove(name);
                return Globals[name] = f();
            }
            throw new XPathUnsupported("unknown variable $" + name);
        }

        private List<object> Flwor(object[] a, Ctx c, int i, Dictionary<string, List<object>> vars)
        {
            var kw = (string)a[0];
            var binds = (List<object>)a[1];
            var ret = (object[])a[2];
            if (i == binds.Count)
            {
                var r = Ev(ret, new Ctx(c.Item, c.Pos, c.Size, vars));
                return kw == "some" || kw == "every" ? L(Ebv(r)) : r;
            }
            var bind = (object[])binds[i];
            var name = (string)bind[0];
            var seq = Ev((object[])bind[1], new Ctx(c.Item, c.Pos, c.Size, vars));
            if (kw == "let") return Flwor(a, c, i + 1, With(vars, name, seq));
            var outp = new List<object>();
            foreach (var item in seq)
            {
                var r = Flwor(a, c, i + 1, With(vars, name, L(item)));
                if (kw == "some") { if ((bool)r[0]) return L(true); }
                else if (kw == "every") { if (!(bool)r[0]) return L(false); }
                else outp.AddRange(r);
            }
            return kw == "some" ? L(false) : (kw == "every" ? L(true) : outp);
        }

        // ---------------------------------------------------------------- paths
        private List<object> PathEv(object[] a, Ctx c)
        {
            var steps = (List<object>)a[2];
            List<object> seq;
            int start;
            if (a[1] != null) { seq = L(Tree.Root); start = 0; }
            else { seq = Ev((object[])steps[0], c); start = 1; }
            for (int s = start; s < steps.Count; s++)
            {
                var step = (object[])steps[s];
                var outp = new List<object>();
                bool allNodes = true;
                int n = seq.Count;
                for (int i = 0; i < n; i++)
                {
                    if (!(seq[i] is Node item)) throw new XPathError("XPTY0019", "path step on an atomic value");
                    var r = (string)step[0] == "axis" ? AxisStep(item, step, c) : Ev(step, new Ctx(item, i + 1, n, c.Vars));
                    foreach (var x in r) { outp.Add(x); if (!(x is Node)) allNodes = false; }
                }
                seq = allNodes ? DocOrder(outp) : outp;
            }
            return seq;
        }

        private (List<object>?, List<object>?) Operands(object[] x, object[] y, Ctx c)
        {
            List<object> l;
            try { l = Atomize(Ev(x, c)); }
            catch (XPathError)
            {
                if (Atomize(Ev(y, c)).Count == 0) return (null, null);
                throw;
            }
            if (l.Count == 0) return (null, null);
            var r = Atomize(Ev(y, c));
            return r.Count == 0 ? (null, null) : (l, r);
        }

        private static List<object> DocOrder(List<object> nodes)
        {
            var by = new SortedDictionary<int, object>();
            foreach (var n in nodes) by[((Node)n).Id] = n;
            return by.Values.ToList();
        }

        private static List<object> NodesOnly(List<object> s)
        {
            foreach (var x in s) if (!(x is Node)) throw new XPathError("XPTY0004", "union of non-nodes");
            return s;
        }

        private static readonly HashSet<string> ReverseAxes = new HashSet<string> { "ancestor", "ancestor-or-self", "preceding", "preceding-sibling", "parent" };

        private List<object> AxisStep(Node n, object[] step, Ctx c)
        {
            var axis = (string)step[1];
            var test = (object[])step[2];
            var preds = (List<object>)step[3];
            IEnumerable<Node> cand;
            switch (axis)
            {
                case "child": cand = n.Children; break;
                case "attribute": cand = n.Attrs; break;
                case "self": cand = new[] { n }; break;
                case "parent": cand = n.Parent != null ? new[] { n.Parent } : Array.Empty<Node>(); break;
                case "descendant": cand = Descendants(n, false); break;
                case "descendant-or-self": cand = Descendants(n, true); break;
                case "ancestor": cand = Ancestors(n, false); break;
                case "ancestor-or-self": cand = Ancestors(n, true); break;
                case "following-sibling": cand = n.Parent != null && n.Kind != Node.ATTR ? n.Parent.Children.Skip(n.SibIndex + 1) : Array.Empty<Node>(); break;
                case "preceding-sibling": cand = n.Parent != null && n.Kind != Node.ATTR ? n.Parent.Children.Take(n.SibIndex).Reverse() : Array.Empty<Node>(); break;
                case "preceding": cand = Preceding(n); break;
                case "following": cand = Following(n); break;
                default: throw new XPathUnsupported("axis " + axis);
            }
            int principal = axis == "attribute" ? Node.ATTR : Node.ELEM;
            var sel = new List<object>();
            foreach (var x in cand) if (TestNode(x, test, principal)) sel.Add(x);
            foreach (var p in preds) sel = ApplyPredicate(sel, (object[])p, c);
            if (ReverseAxes.Contains(axis)) sel.Reverse();
            return sel;
        }

        private static List<Node> Descendants(Node n, bool self)
        {
            var outp = new List<Node>();
            if (self) outp.Add(n);
            var stack = new Stack<Node>();
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                outp.Add(x);
                for (int i = x.Children.Count - 1; i >= 0; i--) stack.Push(x.Children[i]);
            }
            return outp;
        }

        private static List<Node> Ancestors(Node n, bool self)
        {
            var outp = new List<Node>();
            if (self) outp.Add(n);
            for (var p = n.Parent; p != null; p = p.Parent) outp.Add(p);
            return outp;
        }

        private List<Node> Preceding(Node n)
        {
            var anc = new HashSet<int>(Ancestors(n, false).Select(a => a.Id));
            var outp = new List<Node>();
            for (int i = n.Id - 1; i >= 0; i--)
            {
                var x = Tree.All[i];
                if (!anc.Contains(x.Id) && x.Kind != Node.ATTR && x.Kind != Node.DOC) outp.Add(x);
            }
            return outp;
        }

        private List<Node> Following(Node n)
        {
            var start = n;
            while (start.Kind == Node.ATTR) start = start.Parent!;
            var desc = new HashSet<int>(Descendants(start, true).Select(d => d.Id));
            foreach (var at in start.Attrs) desc.Add(at.Id);
            var outp = new List<Node>();
            foreach (var x in Tree.All)
                if (x.Id > start.Id && !desc.Contains(x.Id) && x.Kind != Node.ATTR) outp.Add(x);
            return outp;
        }

        private bool TestNode(Node x, object[] t, int principal)
        {
            switch ((string)t[0])
            {
                case "any": return x.Kind == principal;
                case "name":
                    {
                        if (x.Kind != principal) return false;
                        var qn = (string)t[1];
                        int p = qn.IndexOf(':');
                        if (p >= 0)
                        {
                            var prefix = qn.Substring(0, p);
                            var local = qn.Substring(p + 1);
                            if (!Ns.TryGetValue(prefix, out var uri)) throw new XPathUnsupported("unknown prefix " + prefix);
                            return x.Local == local && x.Ns == uri;
                        }
                        return x.Local == qn && x.Ns == "";
                    }
                case "local": return x.Kind == principal && x.Local == (string)t[1];
                case "nsany":
                    if (!Ns.TryGetValue((string)t[1], out var u2)) throw new XPathUnsupported("unknown prefix " + t[1]);
                    return x.Kind == principal && x.Ns == u2;
                case "kind":
                    switch ((string)t[1])
                    {
                        case "node": return true;
                        case "text": return x.Kind == Node.TEXT;
                        case "element": return x.Kind == Node.ELEM;
                        case "attribute": return x.Kind == Node.ATTR;
                        case "comment": return x.Kind == Node.COMMENT;
                        case "processing-instruction": return x.Kind == Node.PI;
                        case "document-node": return x.Kind == Node.DOC;
                    }
                    break;
            }
            return false;
        }

        private List<object> ApplyPredicate(List<object> seq, object[] p, Ctx c)
        {
            if ((string)p[0] == "lit" && p[1] is long li)
                return li >= 1 && li <= seq.Count ? L(seq[(int)li - 1]) : new List<object>();
            var outp = new List<object>();
            int n = seq.Count;
            for (int i = 0; i < n; i++)
            {
                var item = seq[i];
                List<object> r;
                if (PatternMode)
                {
                    try { r = Ev(p, new Ctx(item, i + 1, n, c.Vars)); }
                    catch (XPathError) { continue; }
                }
                else r = Ev(p, new Ctx(item, i + 1, n, c.Vars));
                if (r.Count == 1 && (r[0] is long || r[0] is double || r[0] is Dec))
                {
                    double num = r[0] is Dec dd ? dd.ToDouble() : r[0] is long ll ? ll : (double)r[0];
                    if (num == i + 1) outp.Add(item);
                }
                else if (Ebv(r)) outp.Add(item);
            }
            return outp;
        }

        // ---------------------------------------------------------------- values
        public List<object> Atomize(List<object> seq)
        {
            var outp = new List<object>(seq.Count);
            foreach (var x in seq) outp.Add(x is Node n ? new Untyped(n.StringValue()) : x);
            return outp;
        }

        public bool Ebv(List<object> seq)
        {
            if (seq.Count == 0) return false;
            var f = seq[0];
            if (f is Node) return true;
            if (seq.Count > 1) throw new XPathError("FORG0006", "effective boolean value of a sequence of atomic values");
            switch (f)
            {
                case bool b: return b;
                case string s: return s.Length > 0;
                case Untyped u: return u.V.Length > 0;
                case long l: return l != 0;
                case double d: return d != 0.0 && !double.IsNaN(d);
                case Dec dc: return !dc.IsZero;
                default: throw new XPathError("FORG0006", "effective boolean value of a date");
            }
        }

        public string Str(object? x)
        {
            switch (x)
            {
                case Node n: return n.StringValue();
                case Untyped u: return u.V;
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case Dec d: return d.ToString();
                case double f: return DoubleToString(f);
                case DateV dv: return dv.Lexical;
                default: return "";
            }
        }

        private static (string Digits, int PointPos) Shortest(double a)
        {
            string r = a.ToString("R", CultureInfo.InvariantCulture);
            int ePos = r.IndexOf('E');
            string mant = ePos >= 0 ? r.Substring(0, ePos) : r;
            int exp = ePos >= 0 ? int.Parse(r.Substring(ePos + 1), CultureInfo.InvariantCulture) : 0;
            int dot = mant.IndexOf('.');
            string digits = dot >= 0 ? mant.Remove(dot, 1) : mant;
            int pointPos = (dot >= 0 ? dot : mant.Length) + exp;
            int lead = 0;
            while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; pointPos--; }
            digits = digits.Substring(lead).TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            return (digits, pointPos);
        }

        public static string DoubleToString(double f)
        {
            if (double.IsNaN(f)) return "NaN";
            if (double.IsInfinity(f)) return f > 0 ? "INF" : "-INF";
            if (f == 0.0) return (1 / f) < 0 ? "-0" : "0";
            double a = Math.Abs(f);
            if (a >= 1e-6 && a < 1e6)
            {
                var d = Dec.FromFloat(f);
                return d == null ? f.ToString("R", CultureInfo.InvariantCulture) : d.ToString();
            }
            var (digits, pointPos) = Shortest(a);
            string m = digits.Length > 1 ? digits[0] + "." + digits.Substring(1) : digits + ".0";
            return (f < 0 ? "-" : "") + m + "E" + (pointPos - 1).ToString(CultureInfo.InvariantCulture);
        }

        private double CastDouble(Untyped u)
        {
            var d = ParseDouble(u.V);
            if (double.IsNaN(d) && u.V.Trim(' ', '\t', '\n', '\r') != "NaN") throw new XPathError("FORG0001", $"cannot convert '{u.V}' to xs:double");
            return d;
        }

        private double ToDouble(object x)
        {
            switch (x)
            {
                case double d: return d;
                case long l: return l;
                case Dec dc: return dc.ToDouble();
                case bool b: return b ? 1.0 : 0.0;
                default: return ParseDouble(Str(x));
            }
        }

        private static readonly Regex DoubleRe = new Regex(@"^[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant);

        public static double ParseDouble(string s)
        {
            var t = s.Trim(' ', '\t', '\n', '\r');
            if (DoubleRe.IsMatch(t)) return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
            switch (t)
            {
                case "INF": case "+INF": return double.PositiveInfinity;
                case "-INF": return double.NegativeInfinity;
                default: return double.NaN;
            }
        }

        private static readonly Regex IntRe = new Regex(@"^[+-]?[0-9]+\z", RegexOptions.CultureInvariant);

        private static long ParseLongSat(string t)
        {
            t = t.TrimStart('+');
            if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)) return v;
            return t.StartsWith("-", StringComparison.Ordinal) ? long.MinValue : long.MaxValue;
        }

        private long ToInt(object x)
        {
            if (x is long l) return l;
            if (x is Untyped || x is string)
            {
                var t = PhpTrim(Str(x));
                if (!IntRe.IsMatch(t)) throw new XPathError("FORG0001", $"not an integer '{t}'");
                return ParseLongSat(t);
            }
            if (x is Dec d) return ParseLongSat(d.Floor().ToString());
            if (x is double f) return double.IsNaN(f) ? 0 : (long)f;
            throw new XPathError("XPTY0004", "not an integer");
        }

        private static string PhpTrim(string s) => s.Trim(' ', '\t', '\n', '\r', '\0', '\x0B');

        private static bool IsNumeric(object x) => x is long || x is double || x is Dec;

        private bool GeneralCompare(string op, List<object> l, List<object> r)
        {
            foreach (var a in l)
            {
                foreach (var b in r)
                {
                    object x, y;
                    if (a is Untyped ua && b is Untyped ub) { x = ua.V; y = ub.V; }
                    else if (a is Untyped ua2 && IsNumeric(b)) { if (UntypedNumericCompare(op, ua2, b, false)) return true; continue; }
                    else if (b is Untyped ub2 && IsNumeric(a)) { if (UntypedNumericCompare(op, ub2, a, true)) return true; continue; }
                    else if (a is Untyped ua3) { x = PromoteUntyped(ua3, b); y = b; }
                    else if (b is Untyped ub3) { x = a; y = PromoteUntyped(ub3, a); }
                    else { x = a; y = b; }
                    if (CompareAtoms(op, x, y)) return true;
                }
            }
            return false;
        }

        private static readonly Regex IntLike = new Regex(@"^[ \t\r\n]*[+-]?[0-9]+[ \t\r\n]*\z", RegexOptions.CultureInvariant);

        private bool UntypedNumericCompare(string op, Untyped u, object num, bool swapped)
        {
            double x, y;
            if (!(num is double) && IntLike.IsMatch(u.V))
            {
                x = ParseLongSat(u.V.Trim(' ', '\t', '\r', '\n', '+'));
                if (Math.Abs(x) >= 9.2e18) x = double.Parse(u.V.Trim(' ', '\t', '\r', '\n'), NumberStyles.Float, CultureInfo.InvariantCulture);
                y = ToDouble(num);
                return swapped ? JavaCompare(op, y, x) : JavaCompare(op, x, y);
            }
            x = CastDouble(u);
            y = ToDouble(num);
            return swapped ? JavaCompare(op, y, x) : JavaCompare(op, x, y);
        }

        private static bool JavaCompare(string op, double x, double y)
        {
            if (op == "=" || op == "!=")
            {
                bool eq = !double.IsNaN(x) && !double.IsNaN(y) && JavaCmp(x, y) == 0;
                return op == "=" ? eq : !eq;
            }
            int c = JavaCmp(x, y);
            switch (op) { case "<": return c < 0; case "<=": return c <= 0; case ">": return c > 0; default: return c >= 0; }
        }

        private static int JavaCmp(double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y)) return (double.IsNaN(x) ? 1 : 0).CompareTo(double.IsNaN(y) ? 1 : 0);
            if (x != y) return x.CompareTo(y);
            return ((1 / x) < 0 ? 0 : 1).CompareTo((1 / y) < 0 ? 0 : 1);
        }

        private object PromoteUntyped(Untyped u, object other)
        {
            if (IsNumeric(other))
            {
                var d = ParseDouble(u.V);
                if (double.IsNaN(d) && PhpTrim(u.V) != "NaN") throw new XPathError("FORG0001", $"cannot convert '{u.V}' to xs:double");
                return d;
            }
            if (other is bool) return Cast(u, "xs:boolean");
            if (other is DateV) return DateV.ParseDate(u.V);
            return u.V;
        }

        private static string TypeName(object x) => x switch
        {
            string _ => "string", long _ => "int", double _ => "float", bool _ => "bool", Dec _ => typeof(Dec).FullName!, DateV _ => typeof(DateV).FullName!,
            Untyped _ => typeof(Untyped).FullName!, Node _ => typeof(Node).FullName!, _ => x.GetType().Name,
        };

        private bool CompareAtoms(string op, object x, object y)
        {
            int c;
            if (IsNumeric(x) && IsNumeric(y))
            {
                if (x is double || y is double)
                {
                    double fx = ToDouble(x), fy = ToDouble(y);
                    if (double.IsNaN(fx) || double.IsNaN(fy)) return op == "!=";
                    c = fx.CompareTo(fy);
                    if (fx == fy) c = 0;
                }
                else c = (x is Dec dx ? dx : Dec.Int((long)x)).Cmp(y is Dec dy ? dy : Dec.Int((long)y));
            }
            else if (x is string sx && y is string sy) c = Math.Sign(CompareUtf8(sx, sy));
            else if (x is bool bx && y is bool by) c = bx.CompareTo(by);
            else if (x is DateV ddx && y is DateV ddy) c = Math.Sign(string.CompareOrdinal(ddx.Key, ddy.Key));
            else throw new XPathError("XPTY0004", "cannot compare " + TypeName(x) + " with " + TypeName(y));
            switch (op)
            {
                case "=": return c == 0; case "!=": return c != 0; case "<": return c < 0;
                case "<=": return c <= 0; case ">": return c > 0; default: return c >= 0;
            }
        }

        /// <summary>strcmp on UTF-8 bytes (= code point order)</summary>
        private static int CompareUtf8(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                int ca = CharExt.ConvertToUtf32OrSelf(a, ref i), cb = CharExt.ConvertToUtf32OrSelf(b, ref j);
                if (ca != cb) return ca < cb ? -1 : 1;
            }
            return (a.Length - i).CompareTo(b.Length - j) == 0 ? 0 : (i >= a.Length && j < b.Length ? -1 : (i < a.Length ? 1 : 0));
        }

        private List<object> Arith(string op, List<object> l0, List<object> r0)
        {
            var l = Atomize(l0); var r = Atomize(r0);
            if (l.Count == 0 || r.Count == 0) return new List<object>();
            if (l.Count > 1 || r.Count > 1) throw new XPathError("XPTY0004", "arithmetic on a sequence");
            object a = l[0] is Untyped ua ? CastDouble(ua) : l[0];
            object b = r[0] is Untyped ub ? CastDouble(ub) : r[0];
            if (!IsNumeric(a) || !IsNumeric(b)) throw new XPathError("XPTY0004", "arithmetic on a non-number");
            if (a is double || b is double)
            {
                double x = ToDouble(a), y = ToDouble(b);
                switch (op)
                {
                    case "+": return L(x + y);
                    case "-": return L(x - y);
                    case "*": return L(x * y);
                    case "div": return L(x / y);
                    case "mod": return L(y == 0.0 ? double.NaN : x % y);
                    case "idiv":
                        if (y == 0.0) throw new XPathError("FOAR0001", "idiv by zero");
                        return L((long)(x / y));
                }
            }
            if (a is long ia && b is long ib && op != "div")
            {
                if (op == "idiv" || op == "mod")
                {
                    if (ib == 0) throw new XPathError("FOAR0001", "division by zero");
                    return L(op == "idiv" ? ia / ib : ia % ib);
                }
                try
                {
                    long res = op == "+" ? checked(ia + ib) : op == "-" ? checked(ia - ib) : checked(ia * ib);
                    return L(res);
                }
                catch (OverflowException) { }
            }
            var dx = a is Dec da ? da : Dec.Int((long)a);
            var dy = b is Dec db ? db : Dec.Int((long)b);
            Dec rr;
            switch (op)
            {
                case "+": rr = dx.Add(dy); break;
                case "-": rr = dx.Sub(dy); break;
                case "*": rr = dx.Mul(dy); break;
                case "div": rr = dx.Div(dy); break;
                case "mod": rr = dx.Mod(dy); break;
                case "idiv": rr = dx.IDiv(dy); break;
                default: throw new XPathUnsupported("operator " + op);
            }
            if (op == "idiv") return L(ParseLongSat(rr.ToString()));
            return L(a is long && b is long && op != "div" && rr.IsInteger ? (object)ParseLongSat(rr.ToString()) : rr);
        }

        private bool InstanceOf(List<object> seq, string type, string occ)
        {
            if (seq.Count == 0) return occ == "?" || occ == "*";
            if (seq.Count > 1 && occ != "*" && occ != "+") return false;
            foreach (var x in seq)
            {
                bool ok;
                switch (type)
                {
                    case "element()": ok = x is Node n1 && n1.Kind == Node.ELEM; break;
                    case "attribute()": ok = x is Node n2 && n2.Kind == Node.ATTR; break;
                    case "text()": ok = x is Node n3 && n3.Kind == Node.TEXT; break;
                    case "node()": ok = x is Node; break;
                    case "xs:string": ok = x is string; break;
                    case "xs:decimal": ok = x is Dec || x is long; break;
                    case "xs:integer": ok = x is long; break;
                    case "xs:double": ok = x is double; break;
                    case "xs:boolean": ok = x is bool; break;
                    case "xs:date": ok = x is DateV; break;
                    case "xs:untypedAtomic": ok = x is Untyped; break;
                    default: throw new XPathUnsupported("instance of " + type);
                }
                if (!ok) return false;
            }
            return true;
        }

        public object Cast(object x, string type)
        {
            if (x is Node nn) x = new Untyped(nn.StringValue());
            var s = Str(x);
            switch (type)
            {
                case "xs:string": return s;
                case "xs:untypedAtomic": return new Untyped(s);
                case "xs:decimal":
                    if (x is Dec) return x;
                    if (x is long l) return Dec.Int(l);
                    if (x is double f) return Dec.FromFloatExact(f) ?? throw new XPathError("FOCA0002", "cannot cast NaN/INF to xs:decimal");
                    if (x is bool b) return Dec.Int(b ? 1 : 0);
                    return Dec.TryParse(s) ?? throw new XPathError("FORG0001", $"cannot cast '{s}' to xs:decimal");
                case "xs:integer":
                    {
                        if (x is long) return x;
                        if (x is Dec || x is double)
                        {
                            var d = x is Dec dd ? dd : (Dec.FromFloatExact((double)x) ?? throw new XPathError("FOCA0002", "NaN to integer"));
                            var t2 = d.Cmp(Dec.Int(0)) < 0 ? d.Ceiling() : d.Floor();
                            return ParseLongSat(t2.ToString());
                        }
                        if (x is bool bb) return bb ? 1L : 0L;
                        var t = s.Trim(' ', '\t', '\n', '\r');
                        if (!IntRe.IsMatch(t)) throw new XPathError("FORG0001", $"cannot cast '{s}' to xs:integer");
                        if (t.TrimStart('+', '-', '0').Length > 18) return Dec.Of(t);
                        return ParseLongSat(t);
                    }
                case "xs:double":
                case "xs:float":
                    {
                        if (IsNumeric(x) || x is bool) return ToDouble(x);
                        var d = ParseDouble(s);
                        if (double.IsNaN(d) && PhpTrim(s) != "NaN") throw new XPathError("FORG0001", $"cannot cast '{s}' to xs:double");
                        return d;
                    }
                case "xs:boolean":
                    if (x is bool) return x;
                    if (IsNumeric(x)) return Ebv(L(x));
                    switch (PhpTrim(s))
                    {
                        case "true": case "1": return true;
                        case "false": case "0": return false;
                        default: throw new XPathError("FORG0001", $"cannot cast '{s}' to xs:boolean");
                    }
                case "xs:date":
                    if (x is DateV) return x;
                    return DateV.ParseDate(s);
            }
            throw new XPathUnsupported("cast to " + type);
        }

        // ---------------------------------------------------------------- functions
        private static List<string> CodePoints(string s)
        {
            var outp = new List<string>();
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { outp.Add(s.Substring(i, 2)); i++; }
                else outp.Add(s[i].ToString());
            }
            return outp;
        }

        private static readonly Regex Ws = new Regex("[ \t\r\n]+", RegexOptions.CultureInvariant);

        private List<object> Call(string name, List<object> args, Ctx c)
        {
            if (Functions.ContainsKey(name)) return UserFn(name, args.Select(a => Ev((object[])a, c)).ToList());
            if (name.StartsWith("fn:", StringComparison.Ordinal)) name = name.Substring(3);
            List<object> A(int i) => Ev((object[])args[i], c);
            string S(int i)
            {
                if (i >= args.Count) return c.Item == null ? "" : Str(c.Item);
                var v = Atomize(Ev((object[])args[i], c));
                if (v.Count > 1) throw new XPathError("XPTY0004", "string argument is a sequence");
                return v.Count > 0 ? Str(v[0]) : "";
            }
            object? One(int i)
            {
                var v = Atomize(Ev((object[])args[i], c));
                if (v.Count > 1) throw new XPathError("XPTY0004", "argument is a sequence");
                return v.Count > 0 ? v[0] : null;
            }
            switch (name)
            {
                case "true": return L(true);
                case "false": return L(false);
                case "not": return L(!Ebv(A(0)));
                case "boolean": return L(Ebv(A(0)));
                case "exists": return L(A(0).Count > 0);
                case "empty": return L(A(0).Count == 0);
                case "count": return L((long)A(0).Count);
                case "position": return L((long)c.Pos);
                case "last": return L((long)c.Size);
                case "current": return Current != null ? L(Current) : new List<object>();
                case "root": return L(Tree.Root);
                case "string":
                    {
                        if (args.Count == 0) return L(c.Item == null ? "" : Str(c.Item));
                        var v = A(0);
                        return L(v.Count > 0 ? Str(v[0]) : "");
                    }
                case "data": return Atomize(A(0));
                case "normalize-space": return L(Ws.Replace(S(0), " ").Trim(' '));
                case "upper-case": return L(WSystem.EInvoice.Support.Py.Upper(S(0)));
                case "lower-case": return L(S(0).ToLowerInvariant());
                case "string-length": return L((long)CodePoints(S(0)).Count);
                case "concat":
                    {
                        var sb = new StringBuilder();
                        for (int i = 0; i < args.Count; i++) sb.Append(S(i));
                        return L(sb.ToString());
                    }
                case "contains": { var h = S(0); var n = S(1); return L(h.IndexOf(n, StringComparison.Ordinal) >= 0); }
                case "starts-with": { var h = S(0); var n = S(1); return L(h.StartsWith(n, StringComparison.Ordinal)); }
                case "ends-with": { var h = S(0); var n = S(1); return L(h.EndsWith(n, StringComparison.Ordinal)); }
                case "substring-after":
                    {
                        var h = S(0); var n = S(1);
                        int p = h.IndexOf(n, StringComparison.Ordinal);
                        return L(p < 0 ? "" : h.Substring(p + n.Length));
                    }
                case "substring-before":
                    {
                        var h = S(0); var n = S(1);
                        int p = h.IndexOf(n, StringComparison.Ordinal);
                        return L(p < 0 || n.Length == 0 ? "" : h.Substring(0, p));
                    }
                case "substring": { var s0 = S(0); var st = One(1); var ln = args.Count > 2 ? One(2) : null; return L(Substring(s0, st, ln, args.Count > 2)); }
                case "translate": { var s0 = S(0); var f = S(1); var t = S(2); return L(Translate(s0, f, t)); }
                case "string-join":
                    {
                        var items = Atomize(A(0)).Select(Str).ToList();
                        var sep = args.Count > 1 ? S(1) : "";
                        return L(string.Join(sep, items));
                    }
                case "matches":
                    {
                        var input = S(0);
                        var re = Regex_(S(1), args.Count > 2 ? S(2) : "");
                        return L(re.IsMatch(input));
                    }
                case "replace":
                    {
                        var input = S(0);
                        var re = Regex_(S(1), args.Count > 3 ? S(3) : "");
                        if (re.IsMatch("")) throw new XPathError("FORX0003", "pattern matches a zero-length string");
                        return L(re.Replace(input, Replacement(S(2))));
                    }
                case "tokenize":
                    {
                        var input = S(0);
                        var re = Regex_(S(1), args.Count > 2 ? S(2) : "");
                        if (re.IsMatch("")) throw new XPathError("FORX0003", "pattern matches a zero-length string");
                        if (input.Length == 0) return new List<object>();
                        var outp = new List<object>();
                        int last = 0;
                        foreach (Match m in re.Matches(input))
                        {
                            outp.Add(input.Substring(last, m.Index - last));
                            last = m.Index + m.Length;
                        }
                        outp.Add(input.Substring(last));
                        return outp;
                    }
                case "string-to-codepoints":
                    {
                        var s0 = S(0);
                        var outp = new List<object>();
                        for (int i = 0; i < s0.Length; i++) { outp.Add((long)CharExt.ConvertToUtf32OrSelf(s0, ref i)); i--; }
                        return outp;
                    }
                case "codepoints-to-string":
                    return L(string.Concat(Atomize(A(0)).Select(x => char.ConvertFromUtf32((int)ToInt(x)))));
                case "reverse": { var v = A(0); v.Reverse(); return v; }
                case "distinct-values":
                    {
                        var outp = new List<object>();
                        foreach (var x0 in Atomize(A(0)))
                        {
                            var x = x0 is Untyped u ? u.V : x0;
                            bool dup = false;
                            foreach (var y in outp)
                            {
                                try { if (CompareAtoms("=", x, y)) { dup = true; break; } }
                                catch (XPathError) { }
                            }
                            if (!dup) outp.Add(x);
                        }
                        return outp;
                    }
                case "index-of":
                    {
                        var outp = new List<object>();
                        var needle = One(1);
                        var hay = Atomize(A(0));
                        for (int i = 0; i < hay.Count; i++)
                        {
                            try { if (needle != null && GeneralCompare("=", L(hay[i]), L(needle))) outp.Add((long)(i + 1)); }
                            catch (XPathError) { }
                        }
                        return outp;
                    }
                case "name":
                case "local-name":
                case "namespace-uri":
                    {
                        object? n = args.Count > 0 ? A(0).FirstOrDefault() : c.Item;
                        if (n == null) return L("");
                        if (!(n is Node nd)) throw new XPathError("XPTY0004", name + "() of a non-node");
                        return L(name == "name" ? nd.QName : (name == "local-name" ? nd.Local : nd.Ns));
                    }
                case "number":
                    {
                        var v = args.Count > 0 ? Atomize(A(0)) : (c.Item == null ? new List<object>() : Atomize(L(c.Item)));
                        if (v.Count == 0) return L(double.NaN);
                        try { return L(v[0] is Untyped || v[0] is string ? ParseDouble(Str(v[0])) : ToDouble(v[0])); }
                        catch (XPathError) { return L(double.NaN); }
                    }
                case "sum":
                    {
                        var v = Atomize(A(0));
                        if (v.Count == 0) return args.Count > 1 ? A(1) : L(0L);
                        object acc = v[0];
                        if (acc is Untyped ua) acc = CastDouble(ua);
                        for (int i = 1; i < v.Count; i++)
                            acc = Arith("+", L(acc), L(v[i] is Untyped ux ? CastDouble(ux) : v[i]))[0];
                        return L(acc);
                    }
                case "avg":
                    {
                        var v = Atomize(A(0));
                        if (v.Count == 0) return new List<object>();
                        var s = Call("sum", new List<object> { args[0] }, c);
                        return Arith("div", s, L((long)v.Count));
                    }
                case "max":
                case "min":
                    {
                        var v = Atomize(A(0)).Select(x => x is Untyped u ? (object)CastDouble(u) : x).ToList();
                        if (v.Count == 0) return new List<object>();
                        var best = v[0];
                        for (int i = 1; i < v.Count; i++) if (CompareAtoms(name == "max" ? ">" : "<", v[i], best)) best = v[i];
                        return L(best);
                    }
                case "abs": case "floor": case "ceiling": case "round": case "round-half-to-even":
                    {
                        var x = One(0);
                        if (x == null) return new List<object>();
                        if (x is Untyped) x = ToDouble(x);
                        long prec = args.Count > 1 ? ToInt(One(1)!) : 0;
                        return L(NumFn(name, x, (int)prec));
                    }
                case "xs:decimal": case "xs:integer": case "xs:string": case "xs:double": case "xs:boolean":
                case "xs:date": case "xs:untypedAtomic": case "xs:float":
                    {
                        var v = Atomize(A(0));
                        if (v.Count == 0) return new List<object>();
                        if (v.Count > 1) throw new XPathError("XPTY0004", name + "() of a sequence");
                        return L(Cast(v[0], name));
                    }
                case "current-date": return L(DateV.ParseDate(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                case "current-dateTime":
                    {
                        var now = DateTimeOffset.Now;
                        return L(new DateV(now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), true));
                    }
                case "document-uri": case "base-uri": return new List<object>();
                case "system-property": return L("");
                case "exactly-one":
                    {
                        var v = A(0);
                        if (v.Count != 1) throw new XPathError("FORG0005", "exactly-one");
                        return v;
                    }
                case "zero-or-one":
                    {
                        var v = A(0);
                        if (v.Count > 1) throw new XPathError("FORG0003", "zero-or-one");
                        return v;
                    }
            }
            throw new XPathUnsupported($"function {name}()");
        }

        private static double PhpRoundFloor(double x) => Math.Floor(x + 0.5);

        private object NumFn(string name, object x, int prec)
        {
            if (x is double f)
            {
                if (double.IsNaN(f) || double.IsInfinity(f)) return f;
                switch (name)
                {
                    case "abs": return Math.Abs(f);
                    case "floor": return Math.Floor(f);
                    case "ceiling": return Math.Ceiling(f);
                    case "round":
                        if (prec == 0)
                        {
                            var r = PhpRoundFloor(f);
                            return r == 0 && (f < 0 || (1 / f) < 0) ? -0.0 : r;
                        }
                        return Dec.FromFloat(f)?.Round(prec).ToDouble() ?? f;
                    case "round-half-to-even": return Dec.FromFloat(f)?.RoundHalfEven(prec).ToDouble() ?? f;
                }
            }
            if (x is long l)
            {
                switch (name)
                {
                    case "abs": return Math.Abs(l);
                    case "round": return prec >= 0 ? l : ParseLongSat(Dec.Int(l).Round(prec).ToString());
                    case "round-half-to-even": return prec >= 0 ? l : ParseLongSat(Dec.Int(l).RoundHalfEven(prec).ToString());
                    default: return l;
                }
            }
            if (x is Dec d)
            {
                switch (name)
                {
                    case "abs": return d.Abs();
                    case "floor": return d.Floor();
                    case "ceiling": return d.Ceiling();
                    case "round": return d.Round(prec);
                    case "round-half-to-even": return d.RoundHalfEven(prec);
                }
            }
            throw new XPathError("XPTY0004", name + "() of a non-number");
        }

        private string Substring(string s, object? start, object? len, bool hasLen)
        {
            var chars = CodePoints(s);
            double st = RoundXp(ToDouble(start is Untyped us ? CastDouble(us) : start ?? double.NaN));
            double ln = !hasLen ? double.PositiveInfinity : RoundXp(ToDouble(len is Untyped ul ? CastDouble(ul) : len ?? double.NaN));
            var sb = new StringBuilder();
            for (int i = 0; i < chars.Count; i++)
            {
                int p = i + 1;
                if (p >= st && p < st + ln) sb.Append(chars[i]);
            }
            return sb.ToString();
        }

        private static double RoundXp(double f) => double.IsNaN(f) || double.IsInfinity(f) ? f : Math.Floor(f + 0.5);

        private static string Translate(string s, string from, string to)
        {
            var f = CodePoints(from); var t = CodePoints(to);
            var map = new Dictionary<string, string>();
            for (int i = 0; i < f.Count; i++) if (!map.ContainsKey(f[i])) map[f[i]] = i < t.Count ? t[i] : "";
            var sb = new StringBuilder();
            foreach (var ch in CodePoints(s)) sb.Append(map.TryGetValue(ch, out var r) ? r : ch);
            return sb.ToString();
        }

        private static readonly Dictionary<string, Regex> RegexCache = new Dictionary<string, Regex>();
        private static readonly Regex XsdOnly = new Regex(@"\\[icIC]|\[[^\]]*-\[", RegexOptions.CultureInvariant);

        /// <summary>XSD/XPath regular expression -> .NET Regex. Unanchored; flags i, m, s, x.</summary>
        private static Regex Regex_(string re, string flags)
        {
            var k = flags + "\0" + re;
            lock (RegexCache)
            {
                if (RegexCache.TryGetValue(k, out var cached)) return cached;
            }
            if (XsdOnly.IsMatch(re)) throw new XPathUnsupported("XSD regex feature in " + re);
            var opts = RegexOptions.CultureInvariant;
            foreach (var ch in flags)
            {
                if (ch == 'i') opts |= RegexOptions.IgnoreCase;
                else if (ch == 'm') opts |= RegexOptions.Multiline;
                else if (ch == 's') opts |= RegexOptions.Singleline;
                else if (ch == 'x') opts |= RegexOptions.IgnorePatternWhitespace;
            }
            Regex r;
            try { r = new Regex(re, opts); }
            catch (ArgumentException) { throw new XPathUnsupported("regex " + re); }
            lock (RegexCache) RegexCache[k] = r;
            return r;
        }

        /// <summary>XPath replacement string ($N groups, \$ and \\ escapes) -> .NET replacement</summary>
        private static string Replacement(string r)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < r.Length; i++)
            {
                char ch = r[i];
                if (ch == '\\' && i + 1 < r.Length && (r[i + 1] == '\\' || r[i + 1] == '$'))
                {
                    sb.Append(r[i + 1] == '$' ? "$$" : "\\");
                    i++;
                }
                else if (ch == '$' && i + 1 < r.Length && char.IsDigit(r[i + 1]))
                {
                    int j = i + 1;
                    while (j < r.Length && char.IsDigit(r[j])) j++;
                    sb.Append("${").Append(r, i + 1, j - i - 1).Append('}');
                    i = j - 1;
                }
                else if (ch == '$') sb.Append("$$");
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        private List<object> UserFn(string name, List<List<object>> argv)
        {
            var f = Functions[name];
            var vars = new Dictionary<string, List<object>>();
            for (int i = 0; i < f.Params.Count; i++)
            {
                var (pname, ptype) = f.Params[i];
                var v = i < argv.Count ? argv[i] : new List<object>();
                if (ptype != null && ptype.StartsWith("xs:", StringComparison.Ordinal))
                {
                    var t = ptype.TrimEnd('?', '*', '+');
                    v = Atomize(v).Select(x => x is Untyped ? Cast(x, t) : (x is long lx && t == "xs:decimal" ? Dec.Int(lx) : x)).ToList();
                }
                vars[pname] = v;
            }
            var c = new Ctx(null, 1, 1, vars);
            foreach (var (vname, ast) in f.Vars) c = new Ctx(null, 1, 1, With(c.Vars, vname, Ev(ast, c)));
            return Ev(f.Body, c);
        }
    }

    internal static class CharExt
    {
        /// <summary>Reads one code point at i and advances i past it.</summary>
        public static int ConvertToUtf32OrSelf(string s, ref int i)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { var cp = char.ConvertToUtf32(s[i], s[i + 1]); i += 2; return cp; }
            return s[i++];
        }
    }
}
