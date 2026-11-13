using Godot;
using Sim.Core.Settlements;
using Sim.Core.World;

namespace MedievalClient;

/// <summary>
/// Draws a settlement's walls, rebuilt only when walls change: pointed timber stakes for a palisade, stone with a
/// walkway for a stone wall, and a tall crenellated wall with towers for a large stone wall. Each tile joins
/// towards its wall neighbours; gates are openings with posts (and doors) or an arch. Planned walls show as a
/// translucent outline.
/// </summary>
public partial class WallRenderer : Node3D
{
    private static readonly Color Stake = new(0.48f, 0.34f, 0.2f);
    private static readonly Color StakeTip = new(0.4f, 0.28f, 0.16f);
    private static readonly Color WallStone = new(0.56f, 0.54f, 0.5f);
    private static readonly Color DarkStone = new(0.42f, 0.41f, 0.38f);
    private static readonly Color Planned = new(0.85f, 0.75f, 0.5f, 0.35f);

    private Settlement _settlement = null!;
    private WorldMap _map = null!;
    private MeshInstance3D _mesh = null!, _plans = null!;
    private int _builtVersion = -1;

    public void Initialize(WorldMap map, Settlement settlement)
    {
        _map = map;
        _settlement = settlement;
        _mesh = new MeshInstance3D();
        _plans = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_mesh);
        AddChild(_plans);
    }

    private bool IsWall(int x, int y) => _settlement.Walls.ContainsKey((x, y));

    public override void _Process(double delta)
    {
        if (_settlement == null || _settlement.WallVersion == _builtVersion) return;
        _builtVersion = _settlement.WallVersion;

        var mb = new MeshBuilder();
        foreach (var ((x, y), wall) in _settlement.Walls)
        {
            float g = TerrainShape.HeightAt(_map, x + 0.5f, y + 0.5f);
            bool e = IsWall(x + 1, y), w = IsWall(x - 1, y), n = IsWall(x, y - 1), s = IsWall(x, y + 1);
            if (wall.Gate) Gate(mb, x, y, g, wall.Tier, alongX: e || w);
            else Segment(mb, x, y, g, wall.Tier, e, w, n, s);
        }
        _mesh.Mesh = mb.Commit(SeasonLook.Default);

        var plans = new MeshBuilder();
        foreach (var plan in _settlement.WallPlans)
        {
            float g = TerrainShape.HeightAt(_map, plan.X + 0.5f, plan.Y + 0.5f);
            plans.Box(new Vector3(plan.X + 0.2f, g, plan.Y + 0.2f), new Vector3(plan.X + 0.8f, g + 0.15f + 0.25f * plan.Tier, plan.Y + 0.8f), Planned);
        }
        _plans.Mesh = plans.Commit(Materials.Overlay);
    }

    private static float Height(byte tier) => tier switch { 0 => 0.8f, 1 => 1.2f, _ => 1.8f };
    private static float Thickness(byte tier) => tier switch { 0 => 0.22f, 1 => 0.4f, _ => 0.55f };

    /// <summary>A wall tile: a core post plus arms towards each neighbouring wall.</summary>
    private static void Segment(MeshBuilder mb, int x, int y, float g, byte tier, bool e, bool w, bool n, bool s)
    {
        float h = Height(tier), t = Thickness(tier) / 2;
        float cx = x + 0.5f, cz = y + 0.5f;
        if (tier == 0)
        {
            // Palisade: a row of pointed stakes along each arm.
            void Stakes(float x0, float z0, float x1, float z1)
            {
                int count = 4;
                for (int i = 0; i <= count; i++)
                {
                    float px = Mathf.Lerp(x0, x1, i / (float)count), pz = Mathf.Lerp(z0, z1, i / (float)count);
                    float sh = h * (0.92f + 0.08f * ((x * 7 + y * 3 + i) % 3));
                    mb.Box(new Vector3(px - 0.07f, g, pz - 0.07f), new Vector3(px + 0.07f, g + sh, pz + 0.07f), Stake);
                    mb.Box(new Vector3(px - 0.04f, g + sh, pz - 0.04f), new Vector3(px + 0.04f, g + sh + 0.12f, pz + 0.04f), StakeTip);
                }
            }
            if (e) Stakes(cx, cz, x + 1f, cz);
            if (w) Stakes(x, cz, cx, cz);
            if (n) Stakes(cx, y, cx, cz);
            if (s) Stakes(cx, cz, cx, y + 1f);
            if (!(e || w || n || s)) Stakes(x + 0.15f, cz, x + 0.85f, cz);
            mb.Box(new Vector3(cx - 0.09f, g, cz - 0.09f), new Vector3(cx + 0.09f, g + h, cz + 0.09f), Stake);
            return;
        }

        // Darker faces with a lighter walkway on top, so the wall reads against pale ground.
        var stone = (tier == 1 ? WallStone : WallStone.Darkened(0.05f)).Darkened(0.18f);
        var top = WallStone.Lightened(0.05f);
        mb.Box(new Vector3(cx - t, g, cz - t), new Vector3(cx + t, g + h, cz + t), stone, top);
        if (e) mb.Box(new Vector3(cx, g, cz - t), new Vector3(x + 1f, g + h, cz + t), stone, top);
        if (w) mb.Box(new Vector3(x, g, cz - t), new Vector3(cx, g + h, cz + t), stone, top);
        if (n) mb.Box(new Vector3(cx - t, g, y), new Vector3(cx + t, g + h, cz), stone, top);
        if (s) mb.Box(new Vector3(cx - t, g, cz), new Vector3(cx + t, g + h, y + 1f), stone, top);
        // Crenellations on top, and on a large wall a tower every few tiles.
        float m = 0.12f;
        mb.Box(new Vector3(cx - t, g + h, cz - t), new Vector3(cx - t + m * 1.4f, g + h + 0.16f, cz + t), DarkStone);
        mb.Box(new Vector3(cx + t - m * 1.4f, g + h, cz - t), new Vector3(cx + t, g + h + 0.16f, cz + t), DarkStone);
        if (tier >= 2 && (x + y) % 8 == 0)
        {
            mb.Box(new Vector3(x + 0.05f, g, y + 0.05f), new Vector3(x + 0.95f, g + h + 0.7f, y + 0.95f), stone.Lightened(0.04f));
            for (int i = 0; i < 4; i++)
            {
                float ox = i % 2 == 0 ? x + 0.05f : x + 0.75f, oz = i < 2 ? y + 0.05f : y + 0.75f;
                mb.Box(new Vector3(ox, g + h + 0.7f, oz), new Vector3(ox + 0.2f, g + h + 0.92f, oz + 0.2f), DarkStone);
            }
        }
    }

    /// <summary>A gate: posts (and open doors) in a palisade, an arched opening in stone.</summary>
    private static void Gate(MeshBuilder mb, int x, int y, float g, byte tier, bool alongX)
    {
        float h = Height(tier) + (tier == 0 ? 0.2f : 0.35f);
        float t = Thickness(tier) / 2 + 0.05f;
        float cx = x + 0.5f, cz = y + 0.5f;
        var color = tier == 0 ? Stake : WallStone.Darkened(0.22f);
        // Two pillars at the tile's ends along the wall, and a lintel across the top.
        if (alongX)
        {
            mb.Box(new Vector3(x, g, cz - t), new Vector3(x + 0.18f, g + h, cz + t), color);
            mb.Box(new Vector3(x + 0.82f, g, cz - t), new Vector3(x + 1f, g + h, cz + t), color);
            mb.Box(new Vector3(x, g + h - 0.18f, cz - t), new Vector3(x + 1f, g + h, cz + t), color);
            if (tier == 0) // doors swung open
            {
                mb.Box(new Vector3(x + 0.18f, g, cz - t - 0.3f), new Vector3(x + 0.24f, g + h - 0.25f, cz - t), Stake.Darkened(0.2f));
                mb.Box(new Vector3(x + 0.76f, g, cz - t - 0.3f), new Vector3(x + 0.82f, g + h - 0.25f, cz - t), Stake.Darkened(0.2f));
            }
        }
        else
        {
            mb.Box(new Vector3(cx - t, g, y), new Vector3(cx + t, g + h, y + 0.18f), color);
            mb.Box(new Vector3(cx - t, g, y + 0.82f), new Vector3(cx + t, g + h, y + 1f), color);
            mb.Box(new Vector3(cx - t, g + h - 0.18f, y), new Vector3(cx + t, g + h, y + 1f), color);
            if (tier == 0)
            {
                mb.Box(new Vector3(cx - t - 0.3f, g, y + 0.18f), new Vector3(cx - t, g + h - 0.25f, y + 0.24f), Stake.Darkened(0.2f));
                mb.Box(new Vector3(cx - t - 0.3f, g, y + 0.76f), new Vector3(cx - t, g + h - 0.25f, y + 0.82f), Stake.Darkened(0.2f));
            }
        }
    }
}
