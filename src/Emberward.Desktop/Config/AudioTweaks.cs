namespace Emberward.Desktop.Config;

/// <summary>Mix levels and limits for sound effects. Gains are relative to the master volume.</summary>
public static class AudioTweaks
{
    public const int SampleRate = 48000;

    /// <summary>Samples per device buffer; about 11 ms at 48 kHz.</summary>
    public const int DeviceBufferSamples = 512;

    /// <summary>
    /// Distance (cells) from the player's tank at which an effect is at about half loudness. The default map is
    /// 320 cells wide, so the far side of it is faint but not silent.
    /// </summary>
    public const float HalfLoudnessDistance = 80f;

    /// <summary>
    /// Inverse-square loudness below which an effect is silent; loudness runs smoothly down to zero there
    /// (about 320 cells away), so nothing cuts in or out at the edge of hearing.
    /// </summary>
    public const float InaudibleGain = 0.06f;

    /// <summary>Horizontal distance (cells) at which an effect is panned fully to one side.</summary>
    public const float FullPanDistance = 130f;

    /// <summary>How far a fully panned effect leans (1 would silence the other speaker).</summary>
    public const float MaxPan = 0.75f;

    public const int MaxVoices = 32;

    /// <summary>At most this many copies of one effect sound at once; a further one replaces the oldest.</summary>
    public const int MaxVoicesPerEffect = 4;

    public const float FireGain = 0.60f;
    public const float ImpactGain = 0.60f;
    public const float DebrisGain = 0.30f;
    public const float TankHitGain = 0.70f;
    public const float TankDeathGain = 1.00f;
    public const float RespawnGain = 0.55f;
    public const float DockGain = 0.45f;
    public const float BuildGain = 0.50f;
    public const float PlantGain = 0.60f;
    public const float RefusedGain = 0.45f;
    public const float PickupGain = 0.40f;
    // A unit of ore unloaded into the network: a quieter pickup, about four a second.
    public const float UnloadGain = 0.18f;
    public const float WarningGain = 0.38f;
    public const float EngineIdleGain = 0.10f;
    public const float EngineMovingGain = 0.22f;
    public const float DigGain = 0.34f;
    public const float TorchGain = 0.30f;
    public const float RechargeGain = 0.14f;

    // Shortest gap between two plays of a repeating effect, in simulation steps (24 per second).
    public const int ImpactsPerStep = 2;
    public const int DebrisIntervalSteps = 6;
    public const int DebrisMinShrapnel = 3;
    public const int PickupIntervalSteps = 3;
    public const int OverheatWarningIntervalSteps = 16;
    public const int ShieldWarningIntervalSteps = 40;
    public const int EnergyWarningIntervalSteps = 56;

    /// <summary>Steps a digging, torching or recharging loop keeps sounding after the last step that fed it.</summary>
    public const int LoopHoldSteps = 4;
}
