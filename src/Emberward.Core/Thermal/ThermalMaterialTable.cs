namespace Emberward.Core.Thermal;

using Emberward.Core.Terrain;

/// <summary>
/// The thermal properties of one <see cref="ThermalMaterial"/>: one row of <see cref="ThermalMaterialTable"/>.
/// </summary>
/// <param name="HeatCapacity">Energy per degree held by a cell of this material (higher heats up slower).</param>
/// <param name="Conductivity">
/// Conductance of half a cell. Two touching cells conduct through both halves in series (see
/// <see cref="ThermalMaterialTable.PairConductance"/>), so two cells of the same material conduct half of this,
/// and a cell next to a much better conductor conducts nearly all of it. For <see cref="ThermalMaterial.Air"/> it is
/// the conductivity of the air field between neighbouring cells.
/// </param>
/// <param name="AirExchange">
/// Conductance between a cell's terrain field and the air field of the same cell. Unused for air, whose terrain
/// field only mirrors its air field.
/// </param>
/// <param name="FixedTemperature">
/// The temperature a cell is pinned to every step, or null for a material that stores heat. A pinned cell is a source
/// or sink: the energy the pin adds or removes is booked in <see cref="ThermalLedger.SunkFixedTemperature"/>.
/// </param>
/// <param name="Loss">
/// The fraction of its temperature above (or below) ambient that a cell's store gives up to the outside every step:
/// a solid cell's terrain field to the deep ground, an open cell's air to the surface. It is what lets a heat or cold
/// source reach an equilibrium with the ground around it instead of spreading for ever. The air row's value applies
/// to open cells only; the air inside a solid cell follows that cell. Booked in
/// <see cref="ThermalLedger.SunkAmbientLoss"/>. Zero for a pinned material, which has no store to lose from.
/// </param>
public readonly record struct ThermalMaterialProperties(
    float HeatCapacity,
    float Conductivity,
    float AirExchange,
    float? FixedTemperature = null,
    float Loss = 0f)
{
    public bool IsFixedTemperature => FixedTemperature.HasValue;
}

/// <summary>
/// One row of <see cref="ThermalMaterialProperties"/> per <see cref="ThermalMaterial"/>, and the conductance of every
/// pair of materials derived from the rows. Adding a material is adding its row; the constructor throws when a
/// material has no row or two.
/// A pair whose tuned conductance the rule cannot produce gets a <see cref="ThermalPairOverride"/>; it applies to
/// both orders of the pair, and the constructor throws on a doubled pair or a non-positive value.
/// </summary>
public sealed class ThermalMaterialTable
{
    private readonly ThermalMaterialProperties[] _rows;
    private readonly float[] _pairConductance;
    private readonly ThermalPairOverride[] _overrides;

    public ThermalMaterialTable(
        IEnumerable<(ThermalMaterial Material, ThermalMaterialProperties Properties)> rows,
        IEnumerable<ThermalPairOverride>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = new ThermalMaterialProperties[Count];
        var seen = new bool[Count];
        foreach (var (material, properties) in rows)
        {
            int index = (int)material;
            if ((uint)index >= (uint)Count)
                throw new ArgumentException($"{material} is not a ThermalMaterial.", nameof(rows));
            if (seen[index])
                throw new ArgumentException($"{material} has two rows.", nameof(rows));
            if (!(properties.Loss >= 0f && properties.Loss < 1f))
                throw new ArgumentException($"{material} has a loss outside [0, 1).", nameof(rows));
            if (properties.IsFixedTemperature && properties.Loss != 0f)
                throw new ArgumentException($"{material} is pinned, so it cannot have a loss.", nameof(rows));
            seen[index] = true;
            _rows[index] = properties;
        }

        for (int i = 0; i < Count; i++)
        {
            if (!seen[i])
                throw new ArgumentException($"{(ThermalMaterial)i} has no row.", nameof(rows));
        }

        _pairConductance = new float[Count * Count];
        for (int a = 0; a < Count; a++)
        {
            for (int b = 0; b < Count; b++)
                _pairConductance[a * Count + b] = SeriesConductance(a, b);
        }

        _overrides = (overrides ?? []).ToArray();
        var overridden = new bool[Count * Count];
        foreach (var (a, b, conductance) in _overrides)
        {
            int ia = (int)a, ib = (int)b;
            if ((uint)ia >= (uint)Count || (uint)ib >= (uint)Count)
                throw new ArgumentException($"Override {a}-{b} names a value that is not a ThermalMaterial.", nameof(overrides));
            if (!(conductance > 0f))
                throw new ArgumentException($"Override {a}-{b} has a non-positive conductance.", nameof(overrides));
            if (overridden[ia * Count + ib])
                throw new ArgumentException($"Pair {a}-{b} is overridden twice.", nameof(overrides));
            overridden[ia * Count + ib] = overridden[ib * Count + ia] = true;
            _pairConductance[ia * Count + ib] = conductance;
            _pairConductance[ib * Count + ia] = conductance;
        }
    }

    /// <summary>Number of <see cref="ThermalMaterial"/> values; the table has one row for each.</summary>
    public static int Count { get; } = Enum.GetValues<ThermalMaterial>().Length;

    public ThermalMaterialProperties this[ThermalMaterial material] => _rows[(int)material];

    /// <summary>The pairs whose conductance is set explicitly instead of derived from the rows.</summary>
    public IReadOnlyList<ThermalPairOverride> Overrides => _overrides;

    /// <summary>The same table with one row replaced (overrides kept), for tests that tune a single material.</summary>
    public ThermalMaterialTable With(ThermalMaterial material, ThermalMaterialProperties properties)
        => new(Enum.GetValues<ThermalMaterial>().Select(m => (m, m == material ? properties : this[m])), _overrides);

    /// <summary>
    /// Conductance between two touching cells: their half-cell conductivities in series,
    /// <c>ka * kb / (ka + kb)</c>, unless the pair has an override. Precomputed for every pair, so the solver pays
    /// one array read.
    /// </summary>
    public float PairConductance(ThermalMaterial a, ThermalMaterial b) => _pairConductance[(int)a * Count + (int)b];

    private float SeriesConductance(int a, int b)
    {
        float ka = _rows[a].Conductivity;
        float kb = _rows[b].Conductivity;
        // Same material: exactly half, without the rounding of the general formula.
        if (a == b)
            return ka * 0.5f;
        float sum = ka + kb;
        return sum > 0f ? ka * kb / sum : 0f;
    }
}

/// <summary>An explicit conductance for the pair <paramref name="A"/>-<paramref name="B"/> (either order).</summary>
public readonly record struct ThermalPairOverride(ThermalMaterial A, ThermalMaterial B, float Conductance);
