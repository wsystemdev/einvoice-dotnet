using System;
using System.Globalization;

namespace WSystem.EInvoice.Support
{
    /// <summary>Invariant double parsing that keeps the sign of zero on every runtime
    /// (.NET Framework's double.Parse("-0") returns +0; .NET Core 3.0+ returns -0).</summary>
    internal static class Num
    {
        private static readonly double NegativeZero = BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL));

        public static double ParseDouble(string s)
        {
            var d = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (d == 0.0 && s.TrimStart(' ', '\t', '\r', '\n').StartsWith("-", StringComparison.Ordinal)) return NegativeZero;
            return d;
        }

        /// <summary>Shortest round-trip invariant text, like .NET Core 3.0+ ToString("R"), on every runtime.</summary>
        public static string RoundTrip(double d)
        {
#if NETSTANDARD2_0
            if (d == 0.0) return 1 / d < 0 ? "-0" : "0";
            if (double.IsNaN(d) || double.IsInfinity(d)) return d.ToString("R", CultureInfo.InvariantCulture);
            for (int p = 1; p <= 17; p++)
            {
                var s = d.ToString("E" + (p - 1), CultureInfo.InvariantCulture);
                if (double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) == d) return Format(s);
            }
            return d.ToString("R", CultureInfo.InvariantCulture);
#else
            return d.ToString("R", CultureInfo.InvariantCulture);
#endif
        }

#if NETSTANDARD2_0
        // "d.ddddE+xxx" -> the layout .NET Core uses: plain digits for 1e-4 <= |d| < 1e17, else "d.dddE+xx" / "dE-xx"
        private static string Format(string e)
        {
            bool neg = e[0] == '-';
            if (neg) e = e.Substring(1);
            int ep = e.IndexOf('E');
            string digits = e.Substring(0, ep).Replace(".", "").TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            int exp = int.Parse(e.Substring(ep + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            string r;
            if (exp > -5 && exp < 17)
            {
                if (exp >= 0)
                    r = digits.Length > exp + 1 ? digits.Substring(0, exp + 1) + "." + digits.Substring(exp + 1) : digits + new string('0', exp + 1 - digits.Length);
                else
                    r = "0." + new string('0', -exp - 1) + digits;
            }
            else
            {
                r = digits.Substring(0, 1) + (digits.Length > 1 ? "." + digits.Substring(1) : "") + "E" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
            }
            return (neg ? "-" : "") + r;
        }
#endif
    }
}
