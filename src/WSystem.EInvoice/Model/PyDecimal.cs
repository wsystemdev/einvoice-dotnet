using System;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using WSystem.EInvoice.Support;

namespace WSystem.EInvoice.Model
{
    /// <summary>
    /// Immutable arbitrary-precision decimal with Python decimal.Decimal semantics for the operations the builders use:
    /// exact add / sub / mul, exact division by powers of ten, quantize(0.01, ROUND_HALF_UP), normalize().
    /// </summary>
    internal sealed class PyDecimal
    {
        private readonly BigInteger _digits; // >= 0
        private readonly int _exp;
        private readonly bool _neg;
        private static readonly Regex NumRe = new Regex(@"^([+-])?(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$", RegexOptions.CultureInvariant);

        private PyDecimal(BigInteger digits, int exp, bool neg) { _digits = digits; _exp = exp; _neg = neg; }

        public static PyDecimal Zero => new PyDecimal(BigInteger.Zero, 0, false);

        /// <summary>Parse like Python Decimal(str(v)). Throws FormatException on junk.</summary>
        public static PyDecimal Of(object? v)
        {
            if (v is PyDecimal p) return p;
            if (v is null || v is bool || v is System.Collections.IEnumerable && !(v is string)) throw new FormatException("not a number");
            string s = v is double d ? Py.FloatRepr(d) : Py.Trim(Py.Str(v));
            var m = NumRe.Match(s);
            if (!m.Success || (m.Groups[2].Value.Length == 0 && m.Groups[3].Value.Length == 0)) throw new FormatException("not a number");
            string intPart = m.Groups[2].Value, frac = m.Groups[3].Value;
            int e = m.Groups[4].Success && m.Groups[4].Value.Length > 0 ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
            string all = (intPart + frac).TrimStart('0');
            var digits = all.Length == 0 ? BigInteger.Zero : BigInteger.Parse(all, CultureInfo.InvariantCulture);
            return new PyDecimal(digits, e - frac.Length, m.Groups[1].Value == "-");
        }

        public bool IsZero => _digits.IsZero;
        public bool IsNegative => _neg && !IsZero;

        private static BigInteger Pow10(int n) => BigInteger.Pow(10, n);

        public PyDecimal Add(PyDecimal o)
        {
            int e = Math.Min(_exp, o._exp);
            var da = _digits * Pow10(_exp - e);
            var db = o._digits * Pow10(o._exp - e);
            bool na = IsNegative, nb = o.IsNegative;
            if (na == nb) return new PyDecimal(da + db, e, na || (_neg && o._neg));
            int c = da.CompareTo(db);
            if (c == 0) return new PyDecimal(BigInteger.Zero, e, false);
            return c > 0 ? new PyDecimal(da - db, e, na) : new PyDecimal(db - da, e, nb);
        }

        public PyDecimal Neg() => new PyDecimal(_digits, _exp, !_neg);
        public PyDecimal Sub(PyDecimal o) => Add(o.Neg());
        public PyDecimal Mul(PyDecimal o) => new PyDecimal(_digits * o._digits, _exp + o._exp, _neg != o._neg);
        public PyDecimal Shift(int n) => new PyDecimal(_digits, _exp - n, _neg);

        public int Cmp(PyDecimal o)
        {
            var d = Sub(o);
            return d.IsZero ? 0 : (d._neg ? -1 : 1);
        }

        /// <summary>quantize to places decimals, ROUND_HALF_UP</summary>
        public PyDecimal Round(int places = 2)
        {
            int target = -places;
            if (_exp >= target) return new PyDecimal(_digits * Pow10(_exp - target), target, _neg);
            int cut = target - _exp;
            var div = Pow10(cut);
            var keep = BigInteger.DivRem(_digits, div, out var rem);
            if (rem * 2 >= div) keep += 1;
            return new PyDecimal(keep, target, _neg);
        }

        private static string Plain(string d, int e)
        {
            if (e >= 0) return d == "0" ? "0" : d + new string('0', e);
            int n = -e;
            if (d.Length <= n) return "0." + new string('0', n - d.Length) + d;
            return d.Substring(0, d.Length - n) + "." + d.Substring(d.Length - n);
        }

        /// <summary>Python format(v.normalize(), "f") with "-0" -> "0"</summary>
        public string Num()
        {
            if (IsZero) return "0";
            string d = _digits.ToString(CultureInfo.InvariantCulture);
            int e = _exp;
            while (e < 0 && d.EndsWith("0", StringComparison.Ordinal)) { d = d.Substring(0, d.Length - 1); e++; }
            return (_neg ? "-" : "") + Plain(d, e);
        }

        public override string ToString() => (_neg ? "-" : "") + Plain(_digits.ToString(CultureInfo.InvariantCulture), _exp);

        /// <summary>quantize(0.01, HALF_UP) then str(); keeps "-0.00"</summary>
        public string A() => Round(2).ToString();

        public string Key() => IsZero ? "0" : Num();

        public decimal ToDecimal() => decimal.Parse(ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
