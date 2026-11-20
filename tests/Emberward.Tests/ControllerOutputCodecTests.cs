namespace Emberward.Tests;

using Emberward.Core.Entities.Bases;
using Emberward.Core.Input;
using Emberward.Core.Types;

public class ControllerOutputCodecTests
{
    [Fact]
    public void EveryButtonCombination_RoundTrips()
    {
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                for (int buttons = 0; buttons < 32; buttons++)
                    for (int upgrade = 0; upgrade <= BaseUpgrades.Count; upgrade++)
                    {
                        var input = new ControllerOutput
                        {
                            MoveSpeed = new Offset(x, y),
                            ShootPrimary = (buttons & 1) != 0,
                            BuildPrimary = (buttons & 2) != 0,
                            BuildSecondary = (buttons & 4) != 0,
                            BuildTertiary = (buttons & 8) != 0,
                            BuildQuaternary = (buttons & 16) != 0,
                            Upgrade = upgrade,
                        };

                        var decoded = ControllerOutputCodec.Decode(ControllerOutputCodec.Encode(input));

                        Assert.Equal(input.MoveSpeed, decoded.MoveSpeed);
                        Assert.Equal(input.ShootPrimary, decoded.ShootPrimary);
                        Assert.Equal(input.BuildPrimary, decoded.BuildPrimary);
                        Assert.Equal(input.BuildSecondary, decoded.BuildSecondary);
                        Assert.Equal(input.BuildTertiary, decoded.BuildTertiary);
                        Assert.Equal(input.BuildQuaternary, decoded.BuildQuaternary);
                        Assert.Equal(input.Upgrade, decoded.Upgrade);
                        Assert.Equal(default, decoded.AimDirection);
                    }
    }

    [Fact]
    public void EncodedInput_FitsIn29Bits()
    {
        var input = new ControllerOutput
        {
            MoveSpeed = new Offset(-1, -1),
            ShootPrimary = true,
            BuildPrimary = true,
            BuildSecondary = true,
            BuildTertiary = true,
            BuildQuaternary = true,
            Upgrade = 4,
            AimDirection = new DirectionF(0.6f, -0.8f),
        };

        Assert.Equal(0u, ControllerOutputCodec.Encode(input) >> 29);
    }

    [Fact]
    public void TheFourthBuildKey_IsAKeyForInputPrediction()
    {
        uint bits = ControllerOutputCodec.Encode(new ControllerOutput { BuildQuaternary = true });
        Assert.NotEqual(0u, bits);
        Assert.Equal(bits, bits & ControllerOutputCodec.KeyMask);
    }

    /// <summary>A decoded aim must encode to the same bits, or two machines could disagree after a re-send.</summary>
    [Fact]
    public void EveryAimStep_SurvivesDecodeAndEncode()
    {
        for (uint step = 0; step < ControllerOutputCodec.AimSteps; step++)
        {
            uint bits = 1u << 11 | step << 12;
            Assert.Equal(bits, ControllerOutputCodec.Encode(ControllerOutputCodec.Decode(bits)));
        }
    }

    [Fact]
    public void QuantizedAim_IsAUnitVectorCloseToTheOriginal()
    {
        for (int i = 0; i < 1000; i++)
        {
            float angle = i * 0.00628f - 3.14f;
            var aim = DirectionF.FromAngle(angle);

            var quantized = ControllerOutputCodec.Quantize(new ControllerOutput { AimDirection = aim }).AimDirection;

            Assert.InRange(quantized.X - aim.X, -1e-4f, 1e-4f);
            Assert.InRange(quantized.Y - aim.Y, -1e-4f, 1e-4f);
            Assert.InRange(quantized.X * quantized.X + quantized.Y * quantized.Y, 0.9999f, 1.0001f);
        }
    }

    [Fact]
    public void Quantize_IsIdempotent()
    {
        var input = new ControllerOutput { MoveSpeed = new Offset(1, -1), AimDirection = new DirectionF(0.3f, 0.7f) };

        var once = ControllerOutputCodec.Quantize(input);
        var twice = ControllerOutputCodec.Quantize(once);

        Assert.Equal(ControllerOutputCodec.Encode(once), ControllerOutputCodec.Encode(twice));
        Assert.Equal(once.AimDirection, twice.AimDirection);
    }

    [Fact]
    public void NoAim_StaysNoAim()
    {
        Assert.Equal(default, ControllerOutputCodec.Quantize(default).AimDirection);
        Assert.Equal(0u, ControllerOutputCodec.Encode(default));
    }
}
