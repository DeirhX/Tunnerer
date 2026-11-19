namespace Emberward.Core.Thermal;

using Emberward.Core.Config;
using Emberward.Core.Terrain;

/// <summary>The sides of a region across which <see cref="TerrainHeatEngine.ComputeRegionDeltaConservative"/> trades heat.</summary>
[Flags]
public enum RegionEdges
{
    None = 0,
    Left = 1,
    Up = 2,
    Right = 4,
    Down = 8,
}

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
        _flowCutoff = settings.ThermalFlowCutoff;
        var materials = settings.ThermalMaterials
            ?? throw new ArgumentException("settings carry no ThermalMaterials table", nameof(settings));
        int count = ThermalMaterialTable.Count;
        _heatCapacity = new float[count];
        _fixedTemperature = new float[count];
        _isFixedTemperature = new bool[count];
        _airExchange = new float[count];
        _loss = new float[count];
        _conductance = new float[count * count];
        for (int a = 0; a < count; a++)
        {
            var row = materials[(ThermalMaterial)a];
            _heatCapacity[a] = row.HeatCapacity;
            _isFixedTemperature[a] = row.IsFixedTemperature;
            _fixedTemperature[a] = row.FixedTemperature ?? 0f;
            _loss[a] = row.Loss;
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
    /// What a cell holds once a step's deltas are applied. This is the only place that decides it, for the full-grid
    /// and the sparse-region solver alike. Returns the terrain field; <paramref name="steppedAir"/> comes back as the
    /// air field.
    /// <list type="bullet">
    /// <item>An open cell's air gives up its material's loss, and its terrain field mirrors the air.</item>
    /// <item>A fixed-temperature cell is pinned; the energy the pin removes is added to <paramref name="sunkFixed"/>.</item>
    /// <item>Any other solid cell gives up its material's loss from the terrain field; the air inside it is left to
    /// follow the cell through the exchange between the two.</item>
    /// </list>
    /// The energy the losses remove is added to <paramref name="sunkLoss"/>.
    /// </summary>
    public float ResolveCell(
        ThermalMaterial material, float steppedTerrain, ref float steppedAir, ref double sunkFixed, ref double sunkLoss)
    {
        if (material == ThermalMaterial.Air)
        {
            float air = steppedAir - _loss[(int)ThermalMaterial.Air] * (steppedAir - _settings.ThermalAmbientTemperature);
            sunkLoss += (double)(steppedAir - air) * _airCapacity;
            steppedAir = air;
            return air;
        }
        if (_isFixedTemperature[(int)material])
        {
            float fixedTemperature = _fixedTemperature[(int)material];
            sunkFixed += FixedCellEnergySunk(material, steppedTerrain, fixedTemperature);
            return fixedTemperature;
        }

        float terrain = steppedTerrain - _loss[(int)material] * (steppedTerrain - _settings.ThermalAmbientTemperature);
        sunkLoss += (double)(steppedTerrain - terrain) * _heatCapacity[(int)material];
        return terrain;
    }

    /// <summary>Energy removed when a fixed-temperature cell is pinned back to <paramref name="fixedTemperature"/>.</summary>
    public double FixedCellEnergySunk(ThermalMaterial material, float terrainTemperature, float fixedTemperature)
        => (double)(terrainTemperature - fixedTemperature) * GetHeatCapacity(material);

    /// <summary>
    /// The one exchange stencil: for every cell of the region, the heat its terrain field trades with its own air and
    /// with the solid cells right and below, and the heat its air trades with the air right and below. Air only moves
    /// between two cells when at least one of them is open: the air inside solid ground does not carry heat from one
    /// solid cell to the next, so ground conducts exactly as its material row says.
    /// <para>
    /// A region's sides are insulated unless <paramref name="exchangeAcross"/> names them. Across a named side each
    /// border cell also trades with the cell just outside, and only the region's half of that trade is written; the
    /// region on the other side writes the other half from the same state, so the two halves cancel exactly. This is
    /// how the solver steps a set of tiles as one seamless area, each tile on its own thread.
    /// </para>
    /// </summary>
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
        float[] airDelta,
        RegionEdges exchangeAcross = RegionEdges.None)
    {
        int rw = maxXInclusive - minX + 1;
        int rh = maxYInclusive - minY + 1;
        int required = rw * rh;
        if (terrainDelta.Length < required || airDelta.Length < required)
            throw new ArgumentException("region delta lengths must cover region area");
        if (minX < 0 || minY < 0 || maxXInclusive >= width || maxYInclusive >= height)
            throw new ArgumentException("region must lie inside the grid");
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
                    ExchangeWithNeighbour(local, local + 1, m1, t1, a1, idx + 1);
                if (y + 1 <= maxYInclusive)
                    ExchangeWithNeighbour(local, local + rw, m1, t1, a1, idx + width);
            }
        }

        if ((exchangeAcross & RegionEdges.Left) != 0 && minX > 0)
            for (int y = minY; y <= maxYInclusive; y++)
                ExchangeAcrossEdge((y - minY) * rw, y * width + minX, -1);
        if ((exchangeAcross & RegionEdges.Right) != 0 && maxXInclusive < width - 1)
            for (int y = minY; y <= maxYInclusive; y++)
                ExchangeAcrossEdge((y - minY) * rw + rw - 1, y * width + maxXInclusive, 1);
        if ((exchangeAcross & RegionEdges.Up) != 0 && minY > 0)
            for (int x = minX; x <= maxXInclusive; x++)
                ExchangeAcrossEdge(x - minX, minY * width + x, -width);
        if ((exchangeAcross & RegionEdges.Down) != 0 && maxYInclusive < height - 1)
            for (int x = minX; x <= maxXInclusive; x++)
                ExchangeAcrossEdge((rh - 1) * rw + x - minX, maxYInclusive * width + x, width);

        // Both halves of a trade between two cells of the region.
        void ExchangeWithNeighbour(int local, int localN, ThermalMaterial m1, float t1, float a1, int idxN)
        {
            ThermalMaterial mN = Pixel.GetThermalMaterial(pixels[idxN]);
            float dQ = TerrainPairFlow(m1, t1, mN, terrainTemperature[idxN]);
            if (dQ != 0f)
            {
                terrainDelta[local] -= dQ / GetHeatCapacity(m1);
                terrainDelta[localN] += dQ / GetHeatCapacity(mN);
            }
            if (m1 != ThermalMaterial.Air && mN != ThermalMaterial.Air)
                return;
            dQ = AirPairFlow(a1, airTemperature[idxN]);
            if (dQ != 0f)
            {
                airDelta[local] -= dQ / _airCapacity;
                airDelta[localN] += dQ / _airCapacity;
            }
        }

        // The region's half of a trade between a border cell and the cell just outside (at idx + step).
        void ExchangeAcrossEdge(int local, int idx, int step)
        {
            ThermalMaterial m1 = Pixel.GetThermalMaterial(pixels[idx]);
            ThermalMaterial mN = Pixel.GetThermalMaterial(pixels[idx + step]);
            float dQ = TerrainPairFlow(m1, terrainTemperature[idx], mN, terrainTemperature[idx + step]);
            if (dQ != 0f)
                terrainDelta[local] -= dQ / GetHeatCapacity(m1);
            if (m1 != ThermalMaterial.Air && mN != ThermalMaterial.Air)
                return;
            dQ = AirPairFlow(airTemperature[idx], airTemperature[idx + step]);
            if (dQ != 0f)
                airDelta[local] -= dQ / _airCapacity;
        }
    }

    /// <summary>
    /// Whether closing one side of a region would matter: true when the heat one border cell trades with the cell
    /// just outside moves either of the two by at least <paramref name="step"/> degrees in a tick. The solver asks
    /// this before it leaves the tile on the other side out of a step. Around settled heat that trade is part of the
    /// balance (a pocket feeding its halo), and a closed side would cut it.
    /// </summary>
    public bool EdgeCarriesHeat(
        float[] terrainTemperature, float[] airTemperature, TerrainPixel[] pixels, int width,
        int minX, int minY, int maxXInclusive, int maxYInclusive, RegionEdges side, float step)
    {
        (int first, int count, int stride, int outward) = side switch
        {
            RegionEdges.Left => (minY * width + minX, maxYInclusive - minY + 1, width, -1),
            RegionEdges.Right => (minY * width + maxXInclusive, maxYInclusive - minY + 1, width, 1),
            RegionEdges.Up => (minY * width + minX, maxXInclusive - minX + 1, 1, -width),
            RegionEdges.Down => (maxYInclusive * width + minX, maxXInclusive - minX + 1, 1, width),
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, "one side at a time"),
        };

        for (int i = 0; i < count; i++)
        {
            int idx = first + i * stride;
            ThermalMaterial m1 = Pixel.GetThermalMaterial(pixels[idx]);
            ThermalMaterial mN = Pixel.GetThermalMaterial(pixels[idx + outward]);
            float dQ = TerrainPairFlow(m1, terrainTemperature[idx], mN, terrainTemperature[idx + outward]);
            if (MathF.Abs(dQ) >= step * MathF.Min(GetHeatCapacity(m1), GetHeatCapacity(mN)))
                return true;
            if (m1 != ThermalMaterial.Air && mN != ThermalMaterial.Air)
                continue;
            dQ = AirPairFlow(airTemperature[idx], airTemperature[idx + outward]);
            if (MathF.Abs(dQ) >= step * _airCapacity)
                return true;
        }
        return false;
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

    /// <summary>
    /// Heat flowing from solid cell A to solid cell B this step (negative the other way); zero if either is open.
    /// Swapping the cells gives exactly the negated value, which is what lets two regions each write their own half.
    /// </summary>
    private float TerrainPairFlow(ThermalMaterial mA, float tA, ThermalMaterial mB, float tB)
    {
        if (mA == ThermalMaterial.Air || mB == ThermalMaterial.Air)
            return 0f;

        float delta = tA - tB;
        if (MathF.Abs(delta) < _flowCutoff)
            return 0f;

        float dQ = StablePairHeatFlow(delta, GetHeatCapacity(mA), GetHeatCapacity(mB), GetConductance(mA, mB));
        return MathF.Abs(dQ) < _flowCutoff ? 0f : dQ;
    }

    /// <summary>Heat flowing from the air of cell A to the air of cell B this step; antisymmetric like the terrain flow.</summary>
    private float AirPairFlow(float aA, float aB)
    {
        float delta = aA - aB;
        if (MathF.Abs(delta) < _flowCutoff)
            return 0f;

        float dQ = StablePairHeatFlow(delta, _airCapacity, _airCapacity, _airPairConductance);
        return MathF.Abs(dQ) < _flowCutoff ? 0f : dQ;
    }

    private void ExchangeTerrainAir(int idx, ThermalMaterial material, float terrainT, float airT, float[] terrainDelta, float[] airDelta)
    {
        // An air cell has no separate terrain state to exchange with: its terrain field mirrors the air field.
        if (material == ThermalMaterial.Air)
            return;

        float delta = terrainT - airT;
        if (MathF.Abs(delta) < _flowCutoff)
            return;

        float cTerrain = GetHeatCapacity(material);
        float cAir = _airCapacity;
        float k = _airExchange[(int)material];
        float dQ = StablePairHeatFlow(delta, cTerrain, cAir, k);
        if (MathF.Abs(dQ) < _flowCutoff)
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
    private readonly float[] _loss;
    private readonly bool[] _isFixedTemperature;
    private readonly float[] _fixedTemperature;
    private readonly float _airCapacity;
    private readonly float _airPairConductance;
    private readonly float _flowCutoff;

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
