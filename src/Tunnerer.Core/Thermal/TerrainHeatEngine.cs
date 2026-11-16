namespace Tunnerer.Core.Thermal;

using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;

/// <summary>
/// Applies one material-heat-exchange simulation step over a terrain heat field.
/// This is kept separate from TerrainGrid to make thermal behavior easier to test in isolation.
/// </summary>
public sealed class TerrainHeatEngine
{
    private readonly SimulationSettings _settings;
    private float[]? _delta;
    private float[]? _airDelta;

    public TerrainHeatEngine()
        : this(SimulationSettings.FromTweaks())
    {
    }

    public TerrainHeatEngine(SimulationSettings settings)
    {
        _settings = settings;
        _heatCapacity = BuildHeatCapacityTable();
        _conductance = BuildConductanceTable();
    }

    public bool AddEnergyAt(float[] temperature, int index, float amount)
    {
        if ((uint)index >= (uint)temperature.Length || amount == 0f)
            return false;

        temperature[index] += amount;
        return true;
    }

    public float GetTemperatureAt(float[] temperature, int len, int index)
    {
        if ((uint)index >= (uint)len)
            return 0f;
        return temperature[index];
    }

    public void StepConservative(float[] terrainTemperature, float[] airTemperature, TerrainPixel[] pixels, int width, int height, ThermalLedger? ledger = null)
    {
        int len = width * height;
        if (terrainTemperature.Length < len || airTemperature.Length < len)
            throw new ArgumentException("temperature lengths must cover simulation grid");

        if (_delta == null || _delta.Length < len)
            _delta = new float[len];
        if (_airDelta == null || _airDelta.Length < len)
            _airDelta = new float[len];

        // The whole grid is one region, so the exchange stencil has a single owner.
        ComputeRegionDeltaConservative(
            terrainTemperature, airTemperature, pixels, width, height,
            0, 0, width - 1, height - 1, _delta, _airDelta);

        double sunkFixed = 0.0;
        for (int i = 0; i < len; i++)
        {
            float nextAir = airTemperature[i] + _airDelta[i];
            terrainTemperature[i] = ResolveTerrainTemperature(
                Pixel.GetThermalMaterial(pixels[i]), terrainTemperature[i] + _delta[i], nextAir, ref sunkFixed);
            airTemperature[i] = nextAir;
        }

        ledger?.RecordSunkFixedTemperature(sunkFixed);
    }

    /// <summary>
    /// What a cell's terrain field holds once a step's deltas are applied. This is the only place that decides it,
    /// for the full-grid and the sparse-region solver alike:
    /// an air cell mirrors its air field; a fixed-temperature cell is pinned (the energy that the pin removes is
    /// added to <paramref name="sunkFixed"/>); every other cell keeps the stepped value.
    /// </summary>
    public float ResolveTerrainTemperature(ThermalMaterial material, float steppedTerrain, float steppedAir, ref double sunkFixed)
    {
        if (material == ThermalMaterial.Air)
            return steppedAir;
        if (!IsFixedTemperatureMaterial(material))
            return steppedTerrain;

        float fixedTemperature = GetFixedTemperature(material);
        sunkFixed += FixedCellEnergySunk(material, steppedTerrain, fixedTemperature);
        return fixedTemperature;
    }

    /// <summary>Energy removed when a fixed-temperature cell is pinned back to <paramref name="fixedTemperature"/>.</summary>
    public double FixedCellEnergySunk(ThermalMaterial material, float terrainTemperature, float fixedTemperature)
        => (double)(terrainTemperature - fixedTemperature) * GetHeatCapacity(material);

    public void ComputeRegionDeltaConservative(
        float[] terrainTemperature,
        float[] airTemperature,
        TerrainPixel[] pixels,
        int width,
        int height,
        int minX,
        int minY,
        int maxXInclusive,
        int maxYInclusive,
        float[] terrainDelta,
        float[] airDelta)
    {
        int rw = maxXInclusive - minX + 1;
        int rh = maxYInclusive - minY + 1;
        int required = rw * rh;
        if (terrainDelta.Length < required || airDelta.Length < required)
            throw new ArgumentException("region delta lengths must cover region area");
        Array.Clear(terrainDelta, 0, required);
        Array.Clear(airDelta, 0, required);

        for (int y = minY; y <= maxYInclusive; y++)
        {
            int row = y * width;
            for (int x = minX; x <= maxXInclusive; x++)
            {
                int idx = row + x;
                int local = (y - minY) * rw + (x - minX);
                ThermalMaterial m1 = Pixel.GetThermalMaterial(pixels[idx]);
                float t1 = terrainTemperature[idx];
                float a1 = airTemperature[idx];

                ExchangeTerrainAir(local, m1, t1, a1, terrainDelta, airDelta);

                if (x + 1 <= maxXInclusive)
                {
                    int idxR = idx + 1;
                    int localR = local + 1;
                    ThermalMaterial mR = Pixel.GetThermalMaterial(pixels[idxR]);
                    float tR = terrainTemperature[idxR];
                    float aR = airTemperature[idxR];
                    ExchangeTerrainPair(local, localR, m1, t1, mR, tR, terrainDelta);
                    ExchangeAirPair(local, localR, a1, aR, airDelta);
                }
                if (y + 1 <= maxYInclusive)
                {
                    int idxD = idx + width;
                    int localD = local + rw;
                    ThermalMaterial mD = Pixel.GetThermalMaterial(pixels[idxD]);
                    float tD = terrainTemperature[idxD];
                    float aD = airTemperature[idxD];
                    ExchangeTerrainPair(local, localD, m1, t1, mD, tD, terrainDelta);
                    ExchangeAirPair(local, localD, a1, aD, airDelta);
                }
            }
        }
    }

    public double SumTotalEnergy(float[] terrainTemperature, float[] airTemperature, TerrainPixel[] pixels)
    {
        if (pixels.Length < terrainTemperature.Length || airTemperature.Length < terrainTemperature.Length)
            throw new ArgumentException("state lengths must cover internal state");

        double sum = 0.0;
        for (int i = 0; i < terrainTemperature.Length; i++)
        {
            // An air cell's terrain field only mirrors its air field, so its energy is counted once (air).
            ThermalMaterial m = Pixel.GetThermalMaterial(pixels[i]);
            if (m != ThermalMaterial.Air)
                sum += terrainTemperature[i] * GetHeatCapacity(m);
            sum += airTemperature[i] * _settings.ThermalCapacityAir;
        }

        return sum;
    }

    private void ExchangeTerrainPair(
        int idxA,
        int idxB,
        ThermalMaterial mA,
        float tA,
        ThermalMaterial mB,
        float tB,
        float[] terrainDelta)
    {
        if (mA == ThermalMaterial.Air || mB == ThermalMaterial.Air)
            return;

        float delta = tA - tB;
        if (MathF.Abs(delta) < 0.0001f)
            return;

        float cA = GetHeatCapacity(mA);
        float cB = GetHeatCapacity(mB);
        float k = GetConductance(mA, mB);
        float dQ = StablePairHeatFlow(delta, cA, cB, k);
        if (MathF.Abs(dQ) < 0.0001f)
            return;

        terrainDelta[idxA] -= dQ / cA;
        terrainDelta[idxB] += dQ / cB;
    }

    private void ExchangeAirPair(int idxA, int idxB, float aA, float aB, float[] airDelta)
    {
        float delta = aA - aB;
        if (MathF.Abs(delta) < 0.0001f)
            return;

        float c = _settings.ThermalCapacityAir;
        float dQ = StablePairHeatFlow(delta, c, c, _settings.ThermalKAirAir * 0.5f + _settings.ThermalAirNeighborCoupling);
        if (MathF.Abs(dQ) < 0.0001f)
            return;

        airDelta[idxA] -= dQ / c;
        airDelta[idxB] += dQ / c;
    }

    private void ExchangeTerrainAir(int idx, ThermalMaterial material, float terrainT, float airT, float[] terrainDelta, float[] airDelta)
    {
        // An air cell has no separate terrain state to exchange with: its terrain field mirrors the air field.
        if (material == ThermalMaterial.Air)
            return;

        float delta = terrainT - airT;
        if (MathF.Abs(delta) < 0.0001f)
            return;

        float cTerrain = GetHeatCapacity(material);
        float cAir = _settings.ThermalCapacityAir;
        float k = GetConductance(material, ThermalMaterial.Air) + 0.5f * _settings.ThermalAirCellCoupling;
        float dQ = StablePairHeatFlow(delta, cTerrain, cAir, k);
        if (MathF.Abs(dQ) < 0.0001f)
            return;

        terrainDelta[idx] -= dQ / cTerrain;
        airDelta[idx] += dQ / cAir;
    }

    private float StablePairHeatFlow(float deltaT, float cA, float cB, float k)
    {
        float dQ = k * deltaT * _settings.ThermalDt;
        float maxStableDQ = MathF.Min(cA, cB) * MathF.Abs(deltaT) * 0.125f;
        if (dQ > maxStableDQ) dQ = maxStableDQ;
        else if (dQ < -maxStableDQ) dQ = -maxStableDQ;
        return dQ;
    }

    // Must equal the number of ThermalMaterial values; SimulationTableGuardTests fails if the two drift apart.
    private const int MaterialCount = 7; // Air, Dirt, Stone, Base, ConstantEnergy, HotRock, FrostRock

    // Per-instance lookup tables, built once from the injected settings so tests can run the engine with custom values.
    private readonly float[] _heatCapacity;
    private readonly float[] _conductance;

    private float[] BuildHeatCapacityTable()
    {
        var table = new float[MaterialCount];
        for (int m = 0; m < MaterialCount; m++)
            table[m] = ComputeHeatCapacity((ThermalMaterial)m);
        return table;
    }

    private float[] BuildConductanceTable()
    {
        var table = new float[MaterialCount * MaterialCount];
        for (int a = 0; a < MaterialCount; a++)
            for (int b = 0; b < MaterialCount; b++)
                table[a * MaterialCount + b] = ComputeConductance((ThermalMaterial)a, (ThermalMaterial)b);
        return table;
    }

    /// <summary>Heat capacity of the terrain field of a cell made of <paramref name="material"/>.</summary>
    public float GetHeatCapacity(ThermalMaterial material) => _heatCapacity[(int)material];

    private float GetConductance(ThermalMaterial a, ThermalMaterial b)
        => _conductance[(int)a * MaterialCount + (int)b];

    private float ComputeHeatCapacity(ThermalMaterial material) => material switch
    {
        ThermalMaterial.Air => _settings.ThermalCapacityAir,
        ThermalMaterial.Dirt => _settings.ThermalCapacityDirt,
        ThermalMaterial.Stone => _settings.ThermalCapacityStone,
        ThermalMaterial.HotRock or ThermalMaterial.FrostRock => _settings.ThermalCapacityStone,
        ThermalMaterial.ConstantEnergy => _settings.ThermalCapacityConstantEnergy,
        _ => _settings.ThermalCapacityBase,
    };

    private float ComputeConductance(ThermalMaterial a, ThermalMaterial b)
    {
        // Hot and frost rock conduct like stone.
        if (a is ThermalMaterial.HotRock or ThermalMaterial.FrostRock) a = ThermalMaterial.Stone;
        if (b is ThermalMaterial.HotRock or ThermalMaterial.FrostRock) b = ThermalMaterial.Stone;
        if (a > b) (a, b) = (b, a);
        return (a, b) switch
        {
            (ThermalMaterial.Air, ThermalMaterial.Air) => _settings.ThermalKAirAir,
            (ThermalMaterial.Air, ThermalMaterial.Dirt) => _settings.ThermalKAirDirt,
            (ThermalMaterial.Air, ThermalMaterial.Stone) => _settings.ThermalKAirStone,
            (ThermalMaterial.Air, ThermalMaterial.Base) => _settings.ThermalKAirBase,
            (ThermalMaterial.Dirt, ThermalMaterial.Dirt) => _settings.ThermalKDirtDirt,
            (ThermalMaterial.Dirt, ThermalMaterial.Stone) => _settings.ThermalKDirtStone,
            (ThermalMaterial.Dirt, ThermalMaterial.Base) => _settings.ThermalKDirtBase,
            (ThermalMaterial.Stone, ThermalMaterial.Stone) => _settings.ThermalKStoneStone,
            (ThermalMaterial.Stone, ThermalMaterial.Base) => _settings.ThermalKStoneBase,
            (ThermalMaterial.Air, ThermalMaterial.ConstantEnergy) => _settings.ThermalKAirConstantEnergy,
            (ThermalMaterial.Dirt, ThermalMaterial.ConstantEnergy) => _settings.ThermalKDirtConstantEnergy,
            (ThermalMaterial.Stone, ThermalMaterial.ConstantEnergy) => _settings.ThermalKStoneConstantEnergy,
            (ThermalMaterial.Base, ThermalMaterial.ConstantEnergy) => _settings.ThermalKBaseConstantEnergy,
            (ThermalMaterial.ConstantEnergy, ThermalMaterial.ConstantEnergy) => _settings.ThermalKConstantEnergyConstantEnergy,
            _ => _settings.ThermalKBaseBase,
        };
    }

    /// <summary>
    /// Fixed-temperature materials hold their temperature whatever flows in or out: base cells are heat sinks
    /// (pinned at ambient), hot rock is a heat source and frost rock a cold source. They are not storage, so sinks and
    /// relays never draw from them.
    /// </summary>
    public static bool IsFixedTemperatureMaterial(ThermalMaterial material)
        => material is ThermalMaterial.Base or ThermalMaterial.ConstantEnergy or ThermalMaterial.HotRock
            or ThermalMaterial.FrostRock;

    public float GetFixedTemperature(ThermalMaterial material)
        => material switch
        {
            ThermalMaterial.Base => _settings.ThermalFixedBaseTemperature,
            ThermalMaterial.ConstantEnergy => _settings.ThermalFixedConstantEnergyTemperature,
            ThermalMaterial.HotRock => _settings.ThermalFixedHotRockTemperature,
            ThermalMaterial.FrostRock => _settings.ThermalFixedFrostRockTemperature,
            _ => 0f,
        };
}
