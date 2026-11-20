namespace Emberward.Core.Input;

using Emberward.Core.Types;

/// <summary>
/// Packs a <see cref="ControllerOutput"/> into 29 bits, the form a player's input takes on the network and in an
/// input log. The aim becomes one of <see cref="AimSteps"/> angles, so an input that will be shared must go through
/// <see cref="Quantize"/> before anyone simulates with it: every machine then feeds Core the same floats.
/// </summary>
/// <remarks>
/// Bits 0-1 move X, 2-3 move Y (each 0, 1 or 3 for -1), 4 shoot, 5-7 the build keys, 8-10 the upgrade key,
/// 11 "has an aim", 12-27 the aim angle, 28 the fourth build key. An input with no aim (a zero direction) stays without one.
/// </remarks>
public static class ControllerOutputCodec
{
    /// <summary>How many directions an encoded aim can take: steps of about 0.0055 degrees.</summary>
    public const int AimSteps = 1 << 16;

    /// <summary>
    /// The bits of an encoded input that are keys: everything but the aim angle. Two inputs equal under this mask
    /// differ only in where they aim.
    /// </summary>
    public const uint KeyMask = ((1u << 12) - 1) | FourthBuildBit;

    private const uint FourthBuildBit = 1u << 28;
    private const int UpgradeMask = 0x7;
    private const uint AimMask = AimSteps - 1;
    private const double Tau = 2.0 * Math.PI;

    public static uint Encode(in ControllerOutput input)
    {
        uint bits = Axis(input.MoveSpeed.X) | Axis(input.MoveSpeed.Y) << 2;
        if (input.ShootPrimary) bits |= 1u << 4;
        if (input.BuildPrimary) bits |= 1u << 5;
        if (input.BuildSecondary) bits |= 1u << 6;
        if (input.BuildTertiary) bits |= 1u << 7;
        if (input.BuildQuaternary) bits |= FourthBuildBit;
        bits |= (uint)(Math.Clamp(input.Upgrade, 0, UpgradeMask) & UpgradeMask) << 8;
        if (input.AimDirection.X != 0 || input.AimDirection.Y != 0)
            bits |= 1u << 11 | AimIndex(input.AimDirection) << 12;
        return bits;
    }

    public static ControllerOutput Decode(uint bits) => new()
    {
        MoveSpeed = new Offset(Axis(bits & 3), Axis(bits >> 2 & 3)),
        ShootPrimary = (bits & 1u << 4) != 0,
        BuildPrimary = (bits & 1u << 5) != 0,
        BuildSecondary = (bits & 1u << 6) != 0,
        BuildTertiary = (bits & 1u << 7) != 0,
        BuildQuaternary = (bits & FourthBuildBit) != 0,
        Upgrade = (int)(bits >> 8 & UpgradeMask),
        AimDirection = (bits & 1u << 11) != 0 ? AimDirection(bits >> 12 & AimMask) : default,
    };

    /// <summary>The input as every machine will see it once it has been encoded and decoded.</summary>
    public static ControllerOutput Quantize(in ControllerOutput input) => Decode(Encode(input));

    private static uint Axis(int speed) => speed > 0 ? 1u : speed < 0 ? 3u : 0u;

    private static int Axis(uint bits) => bits == 1 ? 1 : bits == 3 ? -1 : 0;

    private static uint AimIndex(DirectionF aim)
    {
        double turn = (DeterministicMath.Atan2((double)aim.Y, aim.X) + Math.PI) / Tau;
        return (uint)Math.Round(turn * AimSteps) & AimMask;
    }

    private static DirectionF AimDirection(uint index)
    {
        double angle = index * (Tau / AimSteps) - Math.PI;
        return new DirectionF((float)DeterministicMath.Cos(angle), (float)DeterministicMath.Sin(angle));
    }
}
