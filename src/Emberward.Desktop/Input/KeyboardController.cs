namespace Emberward.Desktop.Input;

using Emberward.Core.Input;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Types;
using Emberward.Desktop.Presentation;
using static SDL3.SDL;

/// <summary>Driving and firing keys; each machine's build key comes from <see cref="MachinePresentation.KeyLabel"/>.</summary>
public readonly record struct KeyBindings(Scancode Left, Scancode Right, Scancode Up, Scancode Down, Scancode Shoot);

public class KeyboardController
{
    private readonly KeyBindings _keys;

    public KeyboardController(KeyBindings keys)
    {
        _keys = keys;
    }

    private static bool IsHeld(Scancode key)
    {
        ReadOnlySpan<bool> keys = GetKeyboardState(out int count);
        int index = (int)key;
        return index < count && keys[index];
    }

    public bool IsLeftHeld => IsHeld(_keys.Left);
    public bool IsRightHeld => IsHeld(_keys.Right);
    public bool IsUpHeld => IsHeld(_keys.Up);
    public bool IsDownHeld => IsHeld(_keys.Down);

    /// <summary>
    /// The machine upgrade panel is open: the number keys buy for the machine next to the tank
    /// (<see cref="ControllerOutput.UpgradeMachine"/>) instead of base upgrades.
    /// </summary>
    public bool MachineUpgradeMode { get; set; }

    public ControllerOutput Poll()
    {
        int dx = 0, dy = 0;
        if (IsHeld(_keys.Left)) dx -= 1;
        if (IsHeld(_keys.Right)) dx += 1;
        if (IsHeld(_keys.Up)) dy -= 1;
        if (IsHeld(_keys.Down)) dy += 1;

        bool primary = false, secondary = false, tertiary = false, quaternary = false;
        foreach (var machine in MachinePresentations.All)
        {
            if (!IsHeld(BuildKeys[(int)machine.Type]))
                continue;
            switch (machine.BuildKey)
            {
                case BuildKey.Primary: primary = true; break;
                case BuildKey.Secondary: secondary = true; break;
                case BuildKey.Tertiary: tertiary = true; break;
                case BuildKey.Quaternary: quaternary = true; break;
            }
        }

        int upgrade = HeldUpgradeKey();
        return new ControllerOutput
        {
            MoveSpeed = new Offset(dx, dy),
            ShootPrimary = IsHeld(_keys.Shoot),
            Recall = IsHeld(Scancode.X),
            BuildPrimary = primary,
            BuildSecondary = secondary,
            BuildTertiary = tertiary,
            BuildQuaternary = quaternary,
            Upgrade = upgrade,
            UpgradeMachine = MachineUpgradeMode && upgrade != 0,
        };
    }

    /// <summary>Each machine's build key, by <see cref="MachineType"/>: the scancode its label names.</summary>
    public static readonly IReadOnlyList<Scancode> BuildKeys =
        MachinePresentations.All.Select(m => Enum.Parse<Scancode>(m.KeyLabel)).ToArray();

    // Number keys 1-7 buy upgrades while docked: 1-5 the base track, 6-7 the tank track (BaseUpgrade order). With the
    // machine upgrade panel open, 1-3 buy the paths of the machine next to the tank instead.
    private static readonly Scancode[] UpgradeKeys =
        [Scancode.Alpha1, Scancode.Alpha2, Scancode.Alpha3, Scancode.Alpha4, Scancode.Alpha5, Scancode.Alpha6, Scancode.Alpha7];

    /// <summary>The upgrade key held (1 for the first), or 0 for none.</summary>
    private static int HeldUpgradeKey()
    {
        for (int i = 0; i < UpgradeKeys.Length; i++)
            if (IsHeld(UpgradeKeys[i]))
                return i + 1;
        return 0;
    }
}
