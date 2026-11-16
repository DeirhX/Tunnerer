namespace Tunnerer.Core.Types;

/// <summary>
/// Transcendental functions that give the same bits on every platform. <see cref="Math.Sin"/>, <see cref="Math.Pow"/>
/// and friends call the C runtime, which rounds differently on Windows than on macOS/Linux, so a simulation that uses
/// them is not reproducible across operating systems. These use only +, -, *, /, <see cref="Math.Round(double)"/>
/// and <see cref="Math.ScaleB"/>, which IEEE 754 and .NET define exactly. Accuracy is within a few ulps of a double,
/// far more than the game needs. Simulation code must call these instead of Math/MathF (SourceGuardTests).
/// </summary>
public static class DeterministicMath
{
    private const double HalfPi = Math.PI / 2;
    // π/2 split into a part with trailing zero bits and the remainder, so k * HalfPiHi is exact (Cody–Waite).
    private const double HalfPiHi = 1.5707963267341256;
    private const double HalfPiLo = 6.077100506506192e-11;
    private const double Ln2Hi = 0.6931471803691238;
    private const double Ln2Lo = 1.9082149292705877e-10;
    private const double Ln2 = Ln2Hi + Ln2Lo;
    private const double Sqrt2 = 1.4142135623730951;
    private const double Sqrt3 = 1.7320508075688772;
    private const double TanPiOver12 = 0.2679491924311227;

    public static double Sin(double x)
    {
        double r = Reduce(x, out int quadrant);
        return (quadrant & 3) switch
        {
            0 => SinPoly(r),
            1 => CosPoly(r),
            2 => -SinPoly(r),
            _ => -CosPoly(r),
        };
    }

    public static double Cos(double x)
    {
        double r = Reduce(x, out int quadrant);
        return (quadrant & 3) switch
        {
            0 => CosPoly(r),
            1 => -SinPoly(r),
            2 => -CosPoly(r),
            _ => SinPoly(r),
        };
    }

    public static float Sin(float x) => (float)Sin((double)x);
    public static float Cos(float x) => (float)Cos((double)x);

    public static double Atan(double x)
    {
        if (double.IsNaN(x))
            return x;
        bool negative = x < 0;
        double a = negative ? -x : x;
        bool inverted = a > 1;
        if (inverted)
            a = 1 / a;
        double offset = 0;
        if (a > TanPiOver12)
        {
            // atan(a) = π/6 + atan((a√3 - 1) / (√3 + a)), which brings a under tan(π/12).
            a = (a * Sqrt3 - 1) / (Sqrt3 + a);
            offset = Math.PI / 6;
        }

        double z = -a * a;
        double sum = OddReciprocals[^1];
        for (int i = OddReciprocals.Length - 2; i >= 0; i--)
            sum = OddReciprocals[i] + z * sum;
        double result = offset + a * sum;
        if (inverted)
            result = HalfPi - result;
        return negative ? -result : result;
    }

    public static double Atan2(double y, double x)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
            return double.NaN;
        if (x > 0)
            return Atan(y / x);
        if (x < 0)
            return y >= 0 ? Atan(y / x) + Math.PI : Atan(y / x) - Math.PI;
        return y > 0 ? HalfPi : y < 0 ? -HalfPi : 0;
    }

    public static float Atan2(float y, float x) => (float)Atan2((double)y, x);

    public static double Exp(double x)
    {
        if (double.IsNaN(x))
            return x;
        if (x > 709.8)
            return double.PositiveInfinity;
        if (x < -745.2)
            return 0;
        double k = Math.Round(x / Ln2);
        double r = (x - k * Ln2Hi) - k * Ln2Lo;
        double sum = InverseFactorials[^1];
        for (int i = InverseFactorials.Length - 2; i >= 0; i--)
            sum = InverseFactorials[i] + r * sum;
        return Math.ScaleB(sum, (int)k);
    }

    public static double Log(double x)
    {
        if (double.IsNaN(x) || x < 0)
            return double.NaN;
        if (x == 0)
            return double.NegativeInfinity;
        if (double.IsPositiveInfinity(x))
            return x;

        int e = Math.ILogB(x);
        double m = Math.ScaleB(x, -e);
        if (m > Sqrt2)
        {
            m /= 2;
            e++;
        }
        // log(m) = 2 atanh(s) with s = (m - 1) / (m + 1), |s| < 0.172.
        double s = (m - 1) / (m + 1);
        double s2 = s * s;
        double sum = OddReciprocals[^1];
        for (int i = OddReciprocals.Length - 2; i >= 0; i--)
            sum = OddReciprocals[i] + s2 * sum;
        return (e * Ln2Hi + 2 * s * sum) + e * Ln2Lo;
    }

    /// <summary>x^y for x ≥ 0 (negative bases are not needed by the game and return NaN unless y is 0).</summary>
    public static double Pow(double x, double y)
    {
        if (y == 0)
            return 1;
        if (x == 0)
            return y > 0 ? 0 : double.PositiveInfinity;
        if (x == 1)
            return 1;
        return Exp(y * Log(x));
    }

    /// <summary>x^n by repeated squaring, for integer exponents; exact rounding order on every platform.</summary>
    public static float Pow(float x, int n)
    {
        bool invert = n < 0;
        uint e = invert ? (uint)-(long)n : (uint)n;
        float result = 1, square = x;
        while (e != 0)
        {
            if ((e & 1) != 0)
                result *= square;
            square *= square;
            e >>= 1;
        }
        return invert ? 1 / result : result;
    }

    private static double Reduce(double x, out int quadrant)
    {
        double k = Math.Round(x / HalfPi);
        quadrant = (int)(long)k;
        return (x - k * HalfPiHi) - k * HalfPiLo;
    }

    // 1/n! and 1/(2n+1). The compiler folds each division to the same double on every platform.
    private static readonly double[] InverseFactorials =
    [
        1.0, 1.0, 1.0 / 2, 1.0 / 6, 1.0 / 24, 1.0 / 120, 1.0 / 720, 1.0 / 5040, 1.0 / 40320, 1.0 / 362880,
        1.0 / 3628800, 1.0 / 39916800, 1.0 / 479001600, 1.0 / 6227020800, 1.0 / 87178291200,
    ];

    private static readonly double[] OddReciprocals =
    [
        1.0, 1.0 / 3, 1.0 / 5, 1.0 / 7, 1.0 / 9, 1.0 / 11, 1.0 / 13, 1.0 / 15, 1.0 / 17, 1.0 / 19, 1.0 / 21, 1.0 / 23,
    ];

    // Taylor series on |r| ≤ π/4 in Horner form; the last term kept is r^17/17! (sin) and r^18/18! (cos).
    private const double S3 = -1.0 / 6, S5 = 1.0 / 120, S7 = -1.0 / 5040, S9 = 1.0 / 362880,
        S11 = -1.0 / 39916800, S13 = 1.0 / 6227020800, S15 = -1.0 / 1307674368000, S17 = 1.0 / 355687428096000;
    private const double C2 = -1.0 / 2, C4 = 1.0 / 24, C6 = -1.0 / 720, C8 = 1.0 / 40320, C10 = -1.0 / 3628800,
        C12 = 1.0 / 479001600, C14 = -1.0 / 87178291200, C16 = 1.0 / 20922789888000, C18 = -1.0 / 6402373705728000;

    private static double SinPoly(double r)
    {
        double z = r * r;
        return r + r * z * (S3 + z * (S5 + z * (S7 + z * (S9 + z * (S11 + z * (S13 + z * (S15 + z * S17)))))));
    }

    private static double CosPoly(double r)
    {
        double z = r * r;
        return 1 + z * (C2 + z * (C4 + z * (C6 + z * (C8 + z * (C10 + z * (C12 + z * (C14 + z * (C16 + z * C18))))))));
    }
}
