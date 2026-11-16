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

    public TerrainHeatEngine()
        : this(SimulationSettings.FromTweaks())
    {
    }

    public TerrainHeatEngine(SimulationSettings settings)
    {
        _settings = settings;
        var materials = settings.ThermalMaterials
            ?? throw new ArgumentException("settings carry no ThermalMaterials table", nameof(settings));
        int count = ThermalMaterialTable.Count;
        _heatCapacity = new float[count];
        _fixedTemperature = new float[count];
        _isFixedTemperature = new bool[count];
        _airExchange = new float[count];
        _conductance = new float[count * count];
        for (int a = 0; a < count; a++)
        {
            var row = materials[(ThermalMaterial)a];
            _heatCapacity[a] = row.HeatCapacity;
            _isFixedTemperature[a] = row.IsFixedTemperature;
            _fixedTemperature[a] = row.FixedTemperature ?? 0f;
            _airExchange[a] = row.AirExchange + 0.5f * settings.ThermalAirCellCoupling;
            for (int b = 0; b < count; b++)
                _conductance[a * count + b] = materials.PairConductance((ThermalMaterial)a, (ThermalMaterial)b);
        }

        _airCapacity = _heatCapacity[(int)ThermalMaterial.Air];
        _airPairConductance = GetConductance(ThermalMaterial.Air, ThermalMaterial.Air) * 0.5f
            + settings.ThermalAirNeighborCoupling;
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
        if (!_isFixedTemperature[(int)material])
            return steppedTerrain;

        float fixedTemperature = _fixedTemperature[(int)material];
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
            sum += airTemperature[i] * _airCapacity;
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

        float c = _airCapacity;
        float dQ = StablePairHeatFlow(delta, c, c, _airPairConductance);
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
        float cAir = _airCapacity;
        float k = _airExchange[(int)material];
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

    // Lookup tables, copied once from the injected settings' material table so the inner loop pays one array read.
    private readonly float[] _heatCapacity;
    private readonly float[] _conductance;
    private readonly float[] _airExchange;
    private readonly bool[] _isFixedTemperature;
    private readonly float[] _fixedTemperature;
    private readonly float _airCapacity;
    private readonly float _airPairConductance;

    /// <summary>Heat capacity of the terrain field of a cell made of <paramref name="material"/>.</summary>
    public float GetHeatCapacity(ThermalMaterial material) => _heatCapacity[(int)material];

    /// <summary>Heat capacity of a cell's air field.</summary>
    public float AirHeatCapacity => _airCapacity;

    private float GetConductance(ThermalMaterial a, ThermalMaterial b)
        => _conductance[(int)a * ThermalMaterialTable.Count + (int)b];

    /// <summary>
    /// Fixed-temperature materials hold their temperature whatever flows in or out: base cells are heat sinks
    /// (pinned at ambient), hot rock is a heat source and frost rock a cold source. They are not storage, so sinks and
    /// relays never draw from them.
    /// </summary>
    public bool IsFixedTemperatureMaterial(ThermalMaterial material) => _isFixedTemperature[(int)material];

    /// <summary>The temperature a fixed-temperature material is pinned to; 0 for any other material.</summary>
    public float GetFixedTemperature(ThermalMaterial material) => _fixedTemperature[(int)material];
}
