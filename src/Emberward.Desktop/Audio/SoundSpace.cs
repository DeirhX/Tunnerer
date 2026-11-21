namespace Emberward.Desktop.Audio;

using Emberward.Desktop.Config;

/// <summary>
/// How something sounds from where the player's tank is: <paramref name="Gain"/> (0 to 1) from distance and
/// obstacles, <paramref name="Pan"/> (-1 left to 1 right) from direction, and <paramref name="Muffle"/> (0 clear to
/// 1 fully muffled) from the ground between the two.
/// </summary>
public readonly record struct Heard(float Gain, float Pan, float Muffle)
{
    public bool IsAudible => Gain > 0f;
}

/// <summary>
/// The one model of where a sound is heard from, shared by the desktop's mixer and the browser: loudness falls off
/// with distance, stereo position follows the direction (so a tank close by on one side sounds from that side), and
/// rock and dirt in the way make a sound quieter and duller, as if heard through the ground.
/// </summary>
public static class SoundSpace
{
    /// <summary>Cutoff of the low-pass filter on a sound that nothing muffles: open, nothing removed.</summary>
    public const float ClearCutoffHz = 20000f;

    /// <summary>How <paramref name="dx"/>, <paramref name="dy"/> cells away with <paramref name="blockingCells"/> of solid ground between sounds.</summary>
    public static Heard Hear(float dx, float dy, int blockingCells)
    {
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        float scaled = distance / AudioTweaks.HalfLoudnessDistance;
        float falloff = 1f / (1f + scaled * scaled);
        float gain = MathF.Max(0f, (falloff - AudioTweaks.InaudibleGain) / (1f - AudioTweaks.InaudibleGain));

        // Pan follows the direction, not the horizontal distance alone; close by it narrows towards the centre so a
        // sound under the tank does not jump from one speaker to the other.
        float pan = dx / MathF.Max(distance, AudioTweaks.FullPanDistance) * AudioTweaks.MaxPan;

        float muffle = Muffle(blockingCells);
        gain *= 1f - AudioTweaks.MuffledGainCut * muffle;
        return new Heard(gain, Math.Clamp(pan, -1f, 1f), muffle);
    }

    /// <summary>
    /// How muffled a sound heard through <paramref name="blockingCells"/> of solid ground is: a thin wall takes the
    /// edge off, a thick one leaves a dull rumble.
    /// </summary>
    public static float Muffle(int blockingCells) =>
        blockingCells <= 0 ? 0f : 1f - MathF.Exp(-blockingCells / AudioTweaks.MuffleCells);

    /// <summary>Cutoff (Hz) of the low-pass filter for <paramref name="muffle"/>, falling evenly in pitch from clear to fully muffled.</summary>
    public static float CutoffHz(float muffle)
    {
        float m = Math.Clamp(muffle, 0f, 1f);
        return ClearCutoffHz * MathF.Pow(AudioTweaks.MuffledCutoffHz / ClearCutoffHz, m);
    }

    /// <summary>
    /// Playback rate for a source whose distance changed by <paramref name="distanceChange"/> cells over a step: a
    /// little higher while it comes closer, lower while it goes away, so an approaching tank can be told by ear.
    /// </summary>
    public static float ApproachRate(float distanceChange) =>
        1f + Math.Clamp(-distanceChange * AudioTweaks.ApproachPitchPerCell, -AudioTweaks.MaxApproachPitch,
            AudioTweaks.MaxApproachPitch);
}
