using Emberward.Core.Types;

namespace Emberward.Tests;

public class DeterministicMathTests
{
    private const double Tolerance = 1e-14;

    [Fact]
    public void SinCos_MatchPlatformMath()
    {
        for (double x = -50; x <= 50; x += 0.0137)
        {
            Assert.Equal(Math.Sin(x), DeterministicMath.Sin(x), Tolerance);
            Assert.Equal(Math.Cos(x), DeterministicMath.Cos(x), Tolerance);
        }
    }

    [Fact]
    public void Atan2_MatchesPlatformMath()
    {
        for (double y = -3; y <= 3; y += 0.071)
            for (double x = -3; x <= 3; x += 0.067)
                Assert.Equal(Math.Atan2(y, x), DeterministicMath.Atan2(y, x), Tolerance);
        Assert.Equal(0, DeterministicMath.Atan2(0.0, 0.0));
        Assert.Equal(Math.PI / 2, DeterministicMath.Atan2(1.0, 0.0), Tolerance);
        Assert.Equal(-Math.PI / 2, DeterministicMath.Atan2(-1.0, 0.0), Tolerance);
    }

    [Fact]
    public void ExpLogPow_MatchPlatformMath()
    {
        for (double x = -30; x <= 30; x += 0.0173)
            Assert.Equal(1, DeterministicMath.Exp(x) / Math.Exp(x), Tolerance);
        for (double x = 1e-6; x <= 1e6; x *= 1.07)
            Assert.Equal(Math.Log(x), DeterministicMath.Log(x), Tolerance * Math.Max(1, Math.Abs(Math.Log(x))));
        for (double x = 0.001; x < 1; x += 0.0037)
            Assert.Equal(Math.Pow(x, 2.5), DeterministicMath.Pow(x, 2.5), Tolerance);
        Assert.Equal(0, DeterministicMath.Pow(0.0, 2.5));
        Assert.Equal(1, DeterministicMath.Pow(0.0, 0.0));
    }

    [Fact]
    public void IntegerPow_MatchesRepeatedMultiplication()
    {
        Assert.Equal(1f, DeterministicMath.Pow(0.7f, 0));
        Assert.Equal(0.7f * 0.7f * 0.7f, DeterministicMath.Pow(0.7f, 3), 1e-7f);
        Assert.Equal(1 / (0.5f * 0.5f), DeterministicMath.Pow(0.5f, -2));
    }

    // Recorded bit patterns: the same on every OS. A failure on one platform only means a call is not deterministic.
    [Theory]
    [InlineData(0.5, 0x3FDEAEE8744B05F0UL, 0x3FEC1528065B7D50UL)]
    [InlineData(2.0, 0x3FED18F6EAD1B446UL, 0xBFDAA22657537205UL)]
    [InlineData(-7.25, 0xBFEA56ADB62A27B8UL, 0x3FE22C6F50DC3FBFUL)]
    public void SinCos_RecordedBits(double x, ulong sinBits, ulong cosBits)
    {
        Assert.Equal($"{sinBits:X16} {cosBits:X16}", $"{(ulong)BitConverter.DoubleToInt64Bits(DeterministicMath.Sin(x)):X16} {(ulong)BitConverter.DoubleToInt64Bits(DeterministicMath.Cos(x)):X16}");
    }
}
