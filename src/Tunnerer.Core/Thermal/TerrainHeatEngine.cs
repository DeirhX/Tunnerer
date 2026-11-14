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

    public bool AddEnergyAt(float[] temperature, int width, int height, int x, int y, float amount)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            return false;
        int idx = x + y * width;
        return AddEnergyAt(temperature, idx, amount);
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
        Array.Clear(_delta, 0, len);
        Array.Clear(_airDelta, 0, len);

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                ThermalMaterial m1 = Pixel.GetThermalMaterial(pixels[idx]);
                float t1 = terrainTemperature[idx];
                float a1 = airTemperature[idx];

                // In-cell coupling between matter and local air volume.
                ExchangeTerrainAir(idx, m1, t1, a1, _delta!, _airDelta!);

                if (x + 1 < width)
                {
                    int idxR = idx + 1;
                    ThermalMaterial mR = Pixel.GetThermalMaterial(pixels[idxR]);
                    float tR = terrainTemperature[idxR];
                    float aR = airTemperature[idxR];
                    ExchangeTerrainPair(idx, idxR, m1, t1, mR, tR, _delta!);
                    ExchangeAirPair(idx, idxR, a1, aR, _airDelta!);
                }
                if (y + 1 < height)
                {
                    int idxD = idx + width;
                    ThermalMaterial mD = Pixel.GetThermalMaterial(pixels[idxD]);
                    float tD = terrainTemperature[idxD];
                    float aD = airTemperature[idxD];
                    ExchangeTerrainPair(idx, idxD, m1, t1, mD, tD, _delta!);
                    ExchangeAirPair(idx, idxD, a1, aD, _airDelta!);
                }
            }
        }

        double sunkFixed = 0.0;
        for (int i = 0; i < len; i++)
        {
            terrainTemperature[i] += _delta[i];
            airTemperature[i] += _airDelta[i];
            ThermalMaterial material = Pixel.GetThermalMaterial(pixels[i]);
            if (material == ThermalMaterial.Air)
            {
                // The terrain field of an air cell is a mirror of its air field and holds no energy.
                terrainTemperature[i] = airTemperature[i];
            }
            else if (IsFixedTemperatureMaterial(material))
            {
                float fixedTemperature = GetFixedTemperature(material);
                sunkFixed += FixedCellEnergySunk(material, terrainTemperature[i], fixedTemperature);
                terrainTemperature[i] = fixedTemperature;
            }
        }

        ledger?.RecordSunkFixedTemperature(sunkFixed);
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

    public void Step(float[] temperature, TerrainPixel[] pixels, int width, int height)
        => Step(temperature, pixels, width, height, includeAmbientExchange: _settings.EnableThermalAmbientExchange);

    public void Step(
        float[] temperature,
        TerrainPixel[] pixels,
        int width,
        int height,
        bool includeAmbientExchange)
    {
        int len = width * height;
        if (temperature.Length < len)
            throw new ArgumentException("temperature length must cover simulation grid", nameof(temperature));

        if (_delta == null || _delta.Length < len)
            _delta = new float[len];
        Array.Clear(_delta, 0, len);

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                ThermalMaterial m1 = Pixel.GetThermalMaterial(pixels[idx]);
                float t1 = temperature[idx];

                if (x + 1 < width)
                    ExchangePair(idx, idx + 1, m1, t1, pixels, temperature);
                if (y + 1 < height)
                    ExchangePair(idx, idx + width, m1, t1, pixels, temperature);
                if (includeAmbientExchange)
                    ExchangeAmbient(idx, m1, t1);
            }
        }

        for (int i = 0; i < len; i++)
        {
            temperature[i] += _delta[i];
        }

        ApplyFixedTemperatureCells(temperature, pixels, len);
    }

    public void ComputeRegionDelta(
        float[] temperature,
        TerrainPixel[] pixels,
        int width,
        int height,
        bool includeAmbientExchange,
        int minX,
        int minY,
        int maxXInclusive,
        int maxYInclusive,
        float[] regionDelta)
    {
        int rw = maxXInclusive - minX + 1;
        int rh = maxYInclusive - minY + 1;
        int required = rw * rh;
        if (regionDelta.Length < required)
            throw new ArgumentException("regionDelta length must cover region area", nameof(regionDelta));
        Array.Clear(regionDelta, 0, required);

        for (int y = minY; y <= maxYInclusive; y++)
        {
            int row = y * width;
            for (int x = minX; x <= maxXInclusive; x++)
            {
                int idx = row + x;
                int local = (y - minY) * rw + (x - minX);
                ThermalMaterial m1 = Pixel.GetThermalMaterial(pixels[idx]);
                float t1 = temperature[idx];

                if (x + 1 <= maxXInclusive)
                    ExchangePairRegion(idx, idx + 1, local, local + 1, m1, t1, pixels, temperature, regionDelta);
                if (y + 1 <= maxYInclusive)
                    ExchangePairRegion(idx, idx + width, local, local + rw, m1, t1, pixels, temperature, regionDelta);
                if (includeAmbientExchange)
                    ExchangeAmbientRegion(local, m1, t1, regionDelta);
            }
        }
    }

    public double SumInternalEnergy(float[] temperature, TerrainPixel[] pixels)
    {
        if (pixels.Length < temperature.Length)
            throw new ArgumentException("pixels length must cover internal state", nameof(pixels));

        double sum = 0.0;
        for (int i = 0; i < temperature.Length; i++)
        {
            ThermalMaterial m = Pixel.GetThermalMaterial(pixels[i]);
            float c = GetHeatCapacity(m);
            sum += temperature[i] * c;
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

    private void ExchangePair(int idxA, int idxB, ThermalMaterial mA, float tA, TerrainPixel[] pixels, float[] temperature)
    {
        ThermalMaterial mB = Pixel.GetThermalMaterial(pixels[idxB]);
        float tB = temperature[idxB];
        float delta = tA - tB;
        if (MathF.Abs(delta) < 0.0001f) return;

        float cA = GetHeatCapacity(mA);
        float cB = GetHeatCapacity(mB);
        float k = GetConductance(mA, mB);
        float dQ = k * delta * _settings.ThermalDt;

        // Keep explicit pair exchange numerically stable under large deltas.
        // Pairwise cap is conservative because each cell can exchange with multiple
        // neighbors in one step; this avoids multi-neighbor overshoot that can
        // create heat after temperature floor clamping.
        float maxStableDQ = MathF.Min(cA, cB) * MathF.Abs(delta) * 0.125f;
        if (dQ > maxStableDQ) dQ = maxStableDQ;
        else if (dQ < -maxStableDQ) dQ = -maxStableDQ;
        if (MathF.Abs(dQ) < 0.0001f) return;

        _delta![idxA] -= dQ / cA;
        _delta[idxB] += dQ / cB;
    }

    private void ExchangePairRegion(
        int idxA,
        int idxB,
        int localA,
        int localB,
        ThermalMaterial mA,
        float tA,
        TerrainPixel[] pixels,
        float[] temperature,
        float[] regionDelta)
    {
        ThermalMaterial mB = Pixel.GetThermalMaterial(pixels[idxB]);
        float tB = temperature[idxB];
        float delta = tA - tB;
        if (MathF.Abs(delta) < 0.0001f)
            return;

        float cA = GetHeatCapacity(mA);
        float cB = GetHeatCapacity(mB);
        float k = GetConductance(mA, mB);
        float dQ = k * delta * _settings.ThermalDt;
        float maxStableDQ = MathF.Min(cA, cB) * MathF.Abs(delta) * 0.125f;
        if (dQ > maxStableDQ) dQ = maxStableDQ;
        else if (dQ < -maxStableDQ) dQ = -maxStableDQ;
        if (MathF.Abs(dQ) < 0.0001f)
            return;

        regionDelta[localA] -= dQ / cA;
        regionDelta[localB] += dQ / cB;
    }

    private void ExchangeAmbient(int idx, ThermalMaterial material, float temperature)
    {
        float ambient = _settings.ThermalAmbientTemperature;
        if (temperature <= ambient)
            return;

        float k = GetAmbientConductance(material);
        float dQ = k * (ambient - temperature) * _settings.ThermalDt;
        if (MathF.Abs(dQ) < 0.0001f) return;

        float capacity = GetHeatCapacity(material);
        _delta![idx] += dQ / capacity;
    }

    private void ExchangeAmbientRegion(int localIdx, ThermalMaterial material, float temperature, float[] regionDelta)
    {
        float ambient = _settings.ThermalAmbientTemperature;
        if (temperature <= ambient)
            return;

        float k = GetAmbientConductance(material);
        float dQ = k * (ambient - temperature) * _settings.ThermalDt;
        if (MathF.Abs(dQ) < 0.0001f)
            return;

        float capacity = GetHeatCapacity(material);
        regionDelta[localIdx] += dQ / capacity;
    }

    private void ApplyFixedTemperatureCells(float[] temperature, TerrainPixel[] pixels, int len)
    {
        for (int i = 0; i < len; i++)
        {
            ThermalMaterial material = Pixel.GetThermalMaterial(pixels[i]);
            if (!IsFixedTemperatureMaterial(material))
                continue;
            float target = GetFixedTemperature(material);
            temperature[i] = target;
        }
    }

    private const int MaterialCount = 6; // Air, Dirt, Stone, Base, ConstantEnergy, HotRock

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
        ThermalMaterial.HotRock => _settings.ThermalCapacityStone,
        ThermalMaterial.ConstantEnergy => _settings.ThermalCapacityConstantEnergy,
        _ => _settings.ThermalCapacityBase,
    };

    private float ComputeConductance(ThermalMaterial a, ThermalMaterial b)
    {
        // Hot rock conducts like stone.
        if (a == ThermalMaterial.HotRock) a = ThermalMaterial.Stone;
        if (b == ThermalMaterial.HotRock) b = ThermalMaterial.Stone;
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

    private float GetAmbientConductance(ThermalMaterial material) => material switch
    {
        ThermalMaterial.Air => _settings.ThermalKAmbientAir,
        ThermalMaterial.Dirt => _settings.ThermalKAmbientDirt,
        ThermalMaterial.Stone => _settings.EnableStoneAmbientExchange ? _settings.ThermalKAmbientStone : 0f,
        ThermalMaterial.ConstantEnergy => _settings.ThermalKAmbientConstantEnergy,
        _ => _settings.ThermalKAmbientBase,
    };

    /// <summary>
    /// Fixed-temperature materials hold their temperature whatever flows in or out: base cells are heat sinks
    /// (pinned at ambient) and hot rock is a heat source. They are not storage, so sinks and relays never draw from them.
    /// </summary>
    public static bool IsFixedTemperatureMaterial(ThermalMaterial material)
        => material == ThermalMaterial.Base || material == ThermalMaterial.ConstantEnergy || material == ThermalMaterial.HotRock;

    public float GetFixedTemperature(ThermalMaterial material)
        => material switch
        {
            ThermalMaterial.Base => _settings.ThermalFixedBaseTemperature,
            ThermalMaterial.ConstantEnergy => _settings.ThermalFixedConstantEnergyTemperature,
            ThermalMaterial.HotRock => _settings.ThermalFixedHotRockTemperature,
            _ => 0f,
        };
}
