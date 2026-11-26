using System;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace WSystem.EInvoice.Validation.Core
{
    /// <summary>Exact xs:decimal. Value = (-1)^neg * digits * 10^-scale, normalised (no trailing fraction zeros).</summary>
    internal sealed class Dec
    {
        private const int DIV_SCALE = 18;
        public readonly bool Neg;
        public readonly BigInteger Digits; // >= 0
        public readonly int Scale;

        private Dec(bool neg, BigInteger digits, int scale) { Neg = neg; Digits = digits; Scale = scale; }

        private static readonly BigInteger Ten = new BigInteger(10);
        private static BigInteger P10(int n) => BigInteger.Pow(Ten, n);

        private static Dec Make(bool neg, BigInteger digits, int scale)
        {
            if (digits.IsZero) return new Dec(false, BigInteger.Zero, 0);
            while (scale > 0 && (digits % Ten).IsZero) { digits /= Ten; scale--; }
            if (scale < 0) { digits *= P10(-scale); scale = 0; }
            return new Dec(neg, digits, scale);
        }

        private static readonly Regex Lex = new Regex(@"^([+-])?([0-9]*)(?:\.([0-9]*))?\z", RegexOptions.CultureInvariant);

        public static Dec? TryParse(string s)
        {
            s = s.Trim(' ', '\t', '\n', '\r');
            var m = Lex.Match(s);
            if (!m.Success || (m.Groups[2].Value.Length == 0 && m.Groups[3].Value.Length == 0)) return null;
            var frac = m.Groups[3].Value;
            var all = (m.Groups[2].Value + frac).TrimStart('0');
            return Make(m.Groups[1].Value == "-", all.Length == 0 ? BigInteger.Zero : BigInteger.Parse(all, CultureInfo.InvariantCulture), frac.Length);
        }

        public static Dec Of(string s) => TryParse(s) ?? throw new ArgumentException("not a decimal: " + s);
        public static Dec Int(long i) => Make(i < 0, BigInteger.Abs(new BigInteger(i)), 0);
        public static Dec FromBig(BigInteger i) => Make(i.Sign < 0, BigInteger.Abs(i), 0);

        public static Dec? FromFloat(double f)
        {
            if (double.IsNaN(f) || double.IsInfinity(f)) return null;
            var s = f.ToString("R", CultureInfo.InvariantCulture);
            if (s.IndexOf('E') >= 0)
            {
                var ex = FromFloatExact(f)!;
                return ex.Scale > 20 ? ex.RoundHalfUpTo(20) : ex;
            }
            return TryParse(s);
        }

        private Dec RoundHalfUpTo(int places)
        {
            int cut = Scale - places;
            var q = BigInteger.DivRem(Digits, P10(cut), out var r);
            if (r * 2 >= P10(cut)) q += 1;
            return Make(Neg, q, places);
        }

        public static Dec? FromFloatExact(double f)
        {
            if (double.IsNaN(f) || double.IsInfinity(f)) return null;
            if (f == 0.0) return Int(0);
            long bits = BitConverter.DoubleToInt64Bits(f);
            bool neg = bits < 0;
            int exp = (int)((bits >> 52) & 0x7FF);
            long man = bits & 0xFFFFFFFFFFFFFL;
            if (exp == 0) exp = 1; else man |= 1L << 52;
            int e = exp - 1075;
            var digits = new BigInteger(man);
            if (e >= 0) return Make(neg, digits * BigInteger.Pow(2, e), 0);
            return Make(neg, digits * BigInteger.Pow(5, -e), -e);
        }

        public bool IsZero => Digits.IsZero;
        public double ToDouble() => double.Parse(ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);

        public override string ToString()
        {
            var d = Digits.ToString(CultureInfo.InvariantCulture);
            if (Scale > 0)
            {
                d = d.PadLeft(Scale + 1, '0');
                d = d.Substring(0, d.Length - Scale) + "." + d.Substring(d.Length - Scale);
            }
            return (Neg ? "-" : "") + d;
        }

        private static (BigInteger, BigInteger, int) Align(Dec a, Dec b)
        {
            int s = Math.Max(a.Scale, b.Scale);
            return (a.Digits * P10(s - a.Scale), b.Digits * P10(s - b.Scale), s);
        }

        public Dec Add(Dec o)
        {
            var (a, b, s) = Align(this, o);
            if (Neg == o.Neg) return Make(Neg, a + b, s);
            int c = a.CompareTo(b);
            if (c == 0) return Make(false, BigInteger.Zero, 0);
            return c > 0 ? Make(Neg, a - b, s) : Make(o.Neg, b - a, s);
        }

        public Dec Negate() => IsZero ? this : new Dec(!Neg, Digits, Scale);
        public Dec Sub(Dec o) => Add(o.Negate());
        public Dec Mul(Dec o) => Make(Neg != o.Neg, Digits * o.Digits, Scale + o.Scale);

        public Dec Div(Dec o)
        {
            if (o.IsZero) throw new XPathError("FOAR0001", "division by zero");
            int scale = Math.Max(DIV_SCALE, Scale - o.Scale + DIV_SCALE);
            var num = Digits * P10(scale + o.Scale - Scale);
            var q = BigInteger.DivRem(num, o.Digits, out var r);
            if (r * 2 > o.Digits) q += 1;
            return Make(Neg != o.Neg, q, scale);
        }

        public Dec IDiv(Dec o)
        {
            if (o.IsZero) throw new XPathError("FOAR0001", "division by zero");
            var (a, b, _) = Align(this, o);
            return Make(Neg != o.Neg, BigInteger.Divide(a, b), 0);
        }

        public Dec Mod(Dec o) => Sub(IDiv(o).Mul(o));

        public int Cmp(Dec o) { var d = Sub(o); return d.IsZero ? 0 : (d.Neg ? -1 : 1); }
        public Dec Abs() => Neg ? Negate() : this;

        public Dec Floor()
        {
            if (Scale == 0) return this;
            var t = Make(Neg, BigInteger.Divide(Digits, P10(Scale)), 0);
            return Neg ? t.Sub(Int(1)) : t;
        }

        public Dec Ceiling() => Negate().Floor().Negate();

        public Dec Round(int precision = 0)
        {
            var f = Make(false, P10(Math.Max(0, precision)), 0);
            return Mul(f).Add(Of("0.5")).Floor().Div(f);
        }

        public Dec RoundHalfEven(int precision = 0)
        {
            var f = Make(false, P10(Math.Max(0, precision)), 0);
            var x = Mul(f);
            var fl = x.Floor();
            var frac = x.Sub(fl);
            int c = frac.Cmp(Of("0.5"));
            if (c > 0 || (c == 0 && fl.Mod(Int(2)).Abs().Cmp(Int(1)) == 0)) fl = fl.Add(Int(1));
            return fl.Div(f);
        }

        public bool IsInteger => Scale == 0;
    }

    /// <summary>xs:untypedAtomic</summary>
    internal sealed class Untyped { public readonly string V; public Untyped(string v) { V = v; } }

    /// <summary>xs:date / xs:dateTime value, compared on a normalised key.</summary>
    internal sealed class DateV
    {
        public readonly string Lexical, Key; public readonly bool DateTime;
        public DateV(string lexical, string key, bool dateTime = false) { Lexical = lexical; Key = key; DateTime = dateTime; }
        private static readonly Regex Re = new Regex(@"^(-?[0-9]{4,})-([0-9]{2})-([0-9]{2})(Z|[+-][0-9]{2}:[0-9]{2})?\z", RegexOptions.CultureInvariant);

        public static DateV ParseDate(string s)
        {
            s = s.Trim(' ', '\t', '\n', '\r', '\0', '\x0B');
            var m = Re.Match(s);
            if (!m.Success || !CheckDate(m.Groups[2].Value, m.Groups[3].Value, m.Groups[1].Value)) throw new XPathError("FORG0001", $"invalid xs:date '{s}'");
            return new DateV(s, $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}");
        }

        private static bool CheckDate(string mo, string d, string y)
        {
            if (!long.TryParse(y, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var yy)) return false;
            int m = int.Parse(mo, CultureInfo.InvariantCulture), dd = int.Parse(d, CultureInfo.InvariantCulture);
            if (yy < 1 || yy > 32767 || m < 1 || m > 12 || dd < 1) return false;
            int[] dim = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
            bool leap = (yy % 4 == 0 && yy % 100 != 0) || yy % 400 == 0;
            int max = m == 2 && leap ? 29 : dim[m - 1];
            return dd <= max;
        }
    }
}
