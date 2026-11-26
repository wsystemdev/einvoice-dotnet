using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WSystem.EInvoice.Support;

namespace WSystem.EInvoice.Model
{
    internal sealed class Ac
    {
        public bool Charge;
        public PyDecimal Amount = PyDecimal.Zero;
        public PyDecimal? Percent;
        public PyDecimal? Base;
        public string Cat = "S";
        public PyDecimal Rate = PyDecimal.Zero;
        public object? Reason;
        public object? ReasonCode;
    }

    internal sealed class Line
    {
        public int Id;
        public PyDecimal Q = PyDecimal.Zero, Price = PyDecimal.Zero, Net = PyDecimal.Zero, Rate = PyDecimal.Zero;
        public string Cat = "S";
        public object? Unit;
        public List<Ac> Acs = new List<Ac>();
        public object? RawDescription;
        public string Name = "";
        public object? Desc;
        public object? Sid;
    }

    internal sealed class VatGroup
    {
        public string Cat = "S";
        public PyDecimal Rate = PyDecimal.Zero, Base = PyDecimal.Zero, Tax = PyDecimal.Zero;
        public bool Ex;
        public object? ExCode, ExText;
    }

    internal sealed class Money
    {
        public string Cur = "EUR";
        public List<Line> Lines = new List<Line>();
        public List<Ac> DocAcs = new List<Ac>();
        public List<VatGroup> Vat = new List<VatGroup>();
        public PyDecimal LineTotal = PyDecimal.Zero, AllowTotal = PyDecimal.Zero, ChargeTotal = PyDecimal.Zero, Basis = PyDecimal.Zero,
            VatTotal = PyDecimal.Zero, Incl = PyDecimal.Zero, Prepaid = PyDecimal.Zero, Payable = PyDecimal.Zero;
        public bool IsCn;
    }

    /// <summary>Shared money model: lines, allowances/charges, VAT groups and totals (same rounding as the W System API).</summary>
    internal static class MoneyModel
    {
        public static void DateCheck(object? j)
        {
            foreach (var f in new[] { "invoice_number", "issue_date", "seller", "buyer", "lines" })
                if (!Py.T(Py.G(j, f))) throw new InputError(f + " is required");
            if (!Codes.IsDate(Py.G(j, "issue_date"))) throw new InputError("issue_date must be YYYY-MM-DD");
        }

        private static readonly Regex Cur3 = new Regex("^[A-Z]{3}\\z", RegexOptions.CultureInvariant);

        public static string Currency(object? j)
        {
            var cur = Py.Upper(Py.T(Py.G(j, "currency")) ? Py.G(j, "currency") : "EUR");
            if (!Cur3.IsMatch(cur)) throw new InputError("currency must be an ISO 4217 code like EUR");
            return cur;
        }

        private static IList<object?> AcList(object? j, string key, string path)
        {
            var v = Py.T(Py.G(j, key)) ? Py.G(j, key) : new List<object?>();
            if (!Py.IsList(v) || Py.AsList(v).Count > 100) throw new InputError(path + " must be a list of up to 100 items");
            return Py.AsList(v);
        }

        private static (PyDecimal, PyDecimal?, PyDecimal?) AcAmount(object? o, string field)
        {
            if (!Py.IsDict(o)) throw new InputError(field + " must be an object");
            PyDecimal? pct = Py.G(o, "percent") != null ? Codes.Dec(Py.G(o, "percent"), field + ".percent") : null;
            PyDecimal? bas = Py.G(o, "base_amount") != null ? Codes.Dec(Py.G(o, "base_amount"), field + ".base_amount") : null;
            PyDecimal amt;
            if (Py.G(o, "amount") != null) amt = Codes.Dec(Py.G(o, "amount"), field + ".amount");
            else if (pct != null && bas != null) amt = bas.Mul(pct).Shift(2);
            else throw new InputError(field + ".amount is required (or percent + base_amount)");
            amt = amt.Round(2);
            if (amt.IsNegative) throw new InputError(field + ".amount must not be negative");
            return (amt, pct, bas);
        }

        private static object? Reason(object? o, bool charge)
        {
            if (Py.T(Py.G(o, "reason"))) return Py.G(o, "reason");
            return Py.T(Py.G(o, "reason_code")) ? null : (charge ? "Zuschlag / Charge" : "Rabatt / Discount");
        }

        public static List<Ac> LineAcs(object? ln, string P)
        {
            var outp = new List<Ac>();
            foreach (var (kind, key) in new[] { (false, "allowances"), (true, "charges") })
            {
                var list = AcList(ln, key, P + "." + key);
                for (int k = 0; k < list.Count; k++)
                {
                    var o = list[k];
                    var (amt, pct, bas) = AcAmount(o, $"{P}.{key}[{k}]");
                    outp.Add(new Ac { Charge = kind, Amount = amt, Percent = pct, Base = bas, Reason = Reason(o, kind), ReasonCode = Py.G(o, "reason_code") });
                }
            }
            return outp;
        }

        private static (string, PyDecimal) CatRate(object? o, string field, (string Cat, PyDecimal Rate)? dflt)
        {
            var cat = Py.Upper(Py.T(Py.G(o, "vat_category")) ? Py.G(o, "vat_category") : (dflt != null ? dflt.Value.Cat : "S"));
            if (Py.G(o, "vat_rate") == null && cat == "S")
            {
                if (dflt != null && dflt.Value.Cat == "S") return (cat, dflt.Value.Rate);
                throw new InputError(field + ".vat_rate is required for VAT category S");
            }
            var rate = Codes.Dec(Py.T(Py.G(o, "vat_rate")) ? Py.G(o, "vat_rate") : (object)0L, field + ".vat_rate");
            return (cat, cat != "O" ? rate : PyDecimal.Zero);
        }

        private sealed class Group { public string Cat = "S"; public PyDecimal Rate = PyDecimal.Zero; public PyDecimal Base = PyDecimal.Zero; }

        private static List<Ac> DocAcs(object? j, List<Group> groups)
        {
            (string, PyDecimal)? only = groups.Count == 1 ? (groups[0].Cat, groups[0].Rate) : ((string, PyDecimal)?)null;
            var outp = new List<Ac>();
            foreach (var (kind, key) in new[] { (false, "allowances"), (true, "charges") })
            {
                var list = AcList(j, key, key);
                for (int k = 0; k < list.Count; k++)
                {
                    var o = list[k];
                    var F = $"{key}[{k}]";
                    var (amt, pct, bas) = AcAmount(o, F);
                    if (Py.G(o, "vat_category") == null && Py.G(o, "vat_rate") == null && only == null)
                        throw new InputError(F + ".vat_category and vat_rate are required when the invoice has more than one VAT rate");
                    var (cat, rate) = CatRate(o, F, only);
                    outp.Add(new Ac { Charge = kind, Amount = amt, Percent = pct, Base = bas, Cat = cat, Rate = rate, Reason = Reason(o, kind), ReasonCode = Py.G(o, "reason_code") });
                }
            }
            return outp;
        }

        private static void AddGroup(List<Group> groups, Dictionary<string, Group> idx, string cat, PyDecimal rate, PyDecimal amount)
        {
            rate = cat != "O" ? rate : PyDecimal.Zero;
            var k = cat + "|" + rate.Key();
            if (!idx.TryGetValue(k, out var g))
            {
                g = new Group { Cat = cat, Rate = rate };
                idx[k] = g;
                groups.Add(g);
            }
            g.Base = g.Base.Add(amount);
        }

        public static Money Compute(object? j, bool checkParties = true)
        {
            DateCheck(j);
            var cur = Currency(j);
            var linesIn = Py.G(j, "lines");
            if (!Py.IsList(linesIn) || Py.AsList(linesIn).Count > 500) throw new InputError("lines must be a list of up to 500 items");
            if (checkParties)
                foreach (var r in new[] { "seller", "buyer" })
                    if (!Py.IsDict(Py.G(j, r))) throw new InputError(r + " must be an object");
            var m = new Money { Cur = cur };
            var groups = new List<Group>();
            var idx = new Dictionary<string, Group>();
            var total = PyDecimal.Zero;
            var list = Py.AsList(linesIn);
            for (int i0 = 0; i0 < list.Count; i0++)
            {
                var ln = list[i0];
                var P = $"lines[{i0}]";
                if (!Py.IsDict(ln) || !Py.T(Py.G(ln, "description"))) throw new InputError(P + ".description is required");
                var q = Codes.Dec(Py.Has(ln, "quantity") ? Py.G(ln, "quantity") : 1L, P + ".quantity");
                var price = Codes.Dec(Py.G(ln, "unit_price"), P + ".unit_price");
                if (price.IsNegative) throw new InputError(P + ".unit_price must not be negative (use a negative quantity)");
                var cat = Py.Upper(Py.T(Py.G(ln, "vat_category")) ? Py.G(ln, "vat_category") : "S");
                if (cat == "S" && Py.G(ln, "vat_rate") == null) throw new InputError(P + ".vat_rate is required for VAT category S");
                var rate = Codes.Dec(Py.T(Py.G(ln, "vat_rate")) ? Py.G(ln, "vat_rate") : 0L, P + ".vat_rate");
                var net = q.Mul(price).Round(2);
                var acs = LineAcs(ln, P);
                foreach (var a in acs) net = net.Add(a.Charge ? a.Amount : a.Amount.Neg());
                total = total.Add(net);
                AddGroup(groups, idx, cat, rate, net);
                m.Lines.Add(new Line
                {
                    Id = i0 + 1, Q = q, Price = price, Net = net, Cat = cat, Rate = rate, Unit = Codes.Unit(Py.G(ln, "unit")), Acs = acs,
                    RawDescription = Py.G(ln, "description"), Name = Py.Cut(Py.Str(Py.G(ln, "description")), 200),
                    Desc = Py.G(ln, "item_description"), Sid = Py.G(ln, "seller_item_id"),
                });
            }
            m.DocAcs = DocAcs(j, groups);
            PyDecimal allow = PyDecimal.Zero, charge = PyDecimal.Zero;
            foreach (var a in m.DocAcs)
            {
                AddGroup(groups, idx, a.Cat, a.Rate, a.Charge ? a.Amount : a.Amount.Neg());
                if (a.Charge) charge = charge.Add(a.Amount); else allow = allow.Add(a.Amount);
            }
            var vatTotal = PyDecimal.Zero;
            foreach (var g in groups)
            {
                var tax = Codes.NO_TAX.Contains(g.Cat) ? PyDecimal.Zero : g.Base.Mul(g.Rate).Shift(2).Round(2);
                vatTotal = vatTotal.Add(tax);
                object? code = null, text = null;
                bool hasEx = Codes.EXEMPT_DEFAULTS.TryGetValue(g.Cat, out var ex);
                if (hasEx)
                {
                    code = ex.Code; text = ex.Text;
                    var vc = Py.G(j, "vat_exemption_code");
                    if (Py.IsDict(vc)) code = Py.Has(vc, g.Cat) ? Py.G(vc, g.Cat) : code;
                    var vr = Py.G(j, "vat_exemption_reason");
                    if (Py.IsDict(vr)) text = Py.Has(vr, g.Cat) ? Py.G(vr, g.Cat) : text;
                }
                m.Vat.Add(new VatGroup { Cat = g.Cat, Rate = g.Rate, Base = g.Base, Tax = tax, Ex = hasEx, ExCode = code, ExText = text });
            }
            m.LineTotal = total; m.AllowTotal = allow; m.ChargeTotal = charge;
            m.Basis = total.Sub(allow).Add(charge);
            m.VatTotal = vatTotal;
            m.Incl = m.Basis.Add(vatTotal);
            m.Prepaid = Codes.Dec(Py.Has(j, "prepaid_amount") ? Py.G(j, "prepaid_amount") : 0L, "prepaid_amount");
            m.Payable = m.Incl.Sub(m.Prepaid);
            m.IsCn = Codes.CN_CODES.Contains(Py.Str(Py.Has(j, "type_code") ? Py.G(j, "type_code") : "380"));
            return m;
        }

        public static Dictionary<string, string> Totals(Money m)
        {
            var t = new Dictionary<string, string>
            {
                ["currency"] = m.Cur, ["line_total"] = m.LineTotal.A(), ["vat_total"] = m.VatTotal.A(),
                ["total_with_vat"] = m.Incl.A(), ["payable"] = m.Payable.A(),
            };
            if (m.DocAcs.Count > 0)
            {
                t["allowance_total"] = m.AllowTotal.A();
                t["charge_total"] = m.ChargeTotal.A();
                t["tax_basis_total"] = m.Basis.A();
            }
            return t;
        }
    }
}
