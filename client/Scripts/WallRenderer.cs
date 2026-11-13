using Godot;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.World;

namespace MedievalClient;

/// <summary>
/// Draws a settlement's walls, rebuilt only when walls change: pointed timber stakes for a palisade; for stone walls
/// a thick wall with a wall-walk on top that soldiers can stand on and walk along, crenellated on the outside with a
/// low parapet inside, stairs up from inside the town, and gatehouses the walk crosses over. Large stone walls are
/// taller and get towers. Each tile joins towards its wall neighbours. Planned walls show as a translucent outline.
/// </summary>
public partial class WallRenderer : Node3D
{
    private static readonly Color Stake = new(0.48f, 0.34f, 0.2f);
    private static readonly Color StakeTip = new(0.4f, 0.28f, 0.16f);
    private static readonly Color WallStone = new(0.56f, 0.54f, 0.5f);
    private static readonly Color DarkStone = new(0.42f, 0.41f, 0.38f);
    private static readonly Color Planned = new(0.85f, 0.75f, 0.5f, 0.35f);

    private Settlement _settlement = null!;
    private GameWorld _world = null!;
    private WorldMap _map = null!;
    private HashSet<(int X, int Y)> _inside = new();
    private MeshInstance3D _mesh = null!, _plans = null!;
    private int _builtVersion = -1;

    public void Initialize(GameWorld world, Settlement settlement)
    {
        _world = world;
        _map = world.Map;
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

        _inside = WallSystem.Enclose(_world, _settlement).Inside;
        var mb = new MeshBuilder();
        foreach (var ((x, y), wall) in _settlement.Walls)
        {
            float g = TerrainShape.HeightAt(_map, x + 0.5f, y + 0.5f);
            bool e = IsWall(x + 1, y), w = IsWall(x - 1, y), n = IsWall(x, y - 1), s = IsWall(x, y + 1);
            bool alongX = e || w || !(n || s);
            if (wall.Piece == WallPiece.Gatehouse)
            {
                Gatehouse(mb, x, y, g, wall.Tier, alongX);
                continue;
            }
            if (wall.Tier == 0)
            {
                if (wall.Gate) Gate(mb, x, y, g, wall.Tier, alongX: e || w);
                else Segment(mb, x, y, g, wall.Tier, e, w, n, s);
                if (wall.Piece == WallPiece.Tower) Watchtower(mb, x, y, g);
                continue;
            }
            StoneWall(mb, x, y, g, wall.Tier, e, w, n, s, wall.Gate);
            if (wall.Piece == WallPiece.Tower) StoneTower(mb, x, y, g, wall.Tier);
            else if (WallSystem.IsStair(_world, _settlement, x, y) && !wall.Gate) Stairs(mb, x, y, g, wall.Tier, alongX: e || w);
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

    /// <summary>A palisade tile: pointed stakes along arms towards each neighbouring wall.</summary>
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

        // Stone walls are drawn by StoneWall.
    }

    // A soldier is about 0.4 tall and 0.17 wide: the walk is wide enough for two abreast, the parapet chest-high.
    private const float ParapetHeight = 0.24f, ParapetThickness = 0.08f;

    private static float StoneHeight(byte tier) => tier >= 2 ? 2.2f : 1.4f;
    private static float StoneHalfWidth(byte tier) => tier >= 2 ? 0.47f : 0.4f;

    /// <summary>Which side of a tile faces into the town (+1 / -1 along the axis), or 0 if unknown.</summary>
    private int InsideSide(int x, int y, bool alongX) =>
        alongX ? (_inside.Contains((x, y + 1)) ? 1 : _inside.Contains((x, y - 1)) ? -1 : 0)
               : (_inside.Contains((x + 1, y)) ? 1 : _inside.Contains((x - 1, y)) ? -1 : 0);

    /// <summary>
    /// A stone wall tile: a thick body whose top is the wall-walk, joined towards neighbouring walls, with parapets
    /// along the open edges (crenellated outside, a low wall inside). In a gate the body becomes a gatehouse: an
    /// archway at ground level with the walk carried over it.
    /// </summary>
    private void StoneWall(MeshBuilder mb, int x, int y, float g, byte tier, bool e, bool w, bool n, bool s, bool gate)
    {
        float h = StoneHeight(tier), t = StoneHalfWidth(tier);
        float cx = x + 0.5f, cz = y + 0.5f;
        var face = (tier >= 2 ? WallStone.Darkened(0.05f) : WallStone).Darkened(0.18f);
        var walk = WallStone.Lightened(0.08f);

        // The body: a core plus arms towards neighbouring walls, so corners and straight runs are solid.
        var parts = new List<(Vector3 Min, Vector3 Max)> { (new(cx - t, g, cz - t), new(cx + t, g + h, cz + t)) };
        if (e) parts.Add((new(cx, g, cz - t), new(x + 1f, g + h, cz + t)));
        if (w) parts.Add((new(x, g, cz - t), new(cx, g + h, cz + t)));
        if (n) parts.Add((new(cx - t, g, y), new(cx + t, g + h, cz)));
        if (s) parts.Add((new(cx - t, g, cz), new(cx + t, g + h, y + 1f)));
        bool alongX = e || w || !(n || s);
        if (gate)
        {
            // Gatehouse: piers on either side of a passage, the walk above it on a deep lintel.
            float open = 0.32f, lintel = h - 0.55f;
            if (alongX)
            {
                mb.Box(new Vector3(x, g, cz - t - 0.06f), new Vector3(cx - open, g + h, cz + t + 0.06f), face, walk);
                mb.Box(new Vector3(cx + open, g, cz - t - 0.06f), new Vector3(x + 1f, g + h, cz + t + 0.06f), face, walk);
                mb.Box(new Vector3(cx - open, g + lintel, cz - t - 0.06f), new Vector3(cx + open, g + h, cz + t + 0.06f), face, walk);
            }
            else
            {
                mb.Box(new Vector3(cx - t - 0.06f, g, y), new Vector3(cx + t + 0.06f, g + h, cz - open), face, walk);
                mb.Box(new Vector3(cx - t - 0.06f, g, cz + open), new Vector3(cx + t + 0.06f, g + h, y + 1f), face, walk);
                mb.Box(new Vector3(cx - t - 0.06f, g + lintel, cz - open), new Vector3(cx + t + 0.06f, g + h, cz + open), face, walk);
            }
        }
        else
        {
            foreach (var (min, max) in parts) mb.Box(min, max, face, walk);
        }

        // Parapets along the long edges of the walk: battlements outside, a plain low wall inside.
        int inside = InsideSide(x, y, alongX);
        float top = g + h;
        void Run(Vector3 from, Vector3 to, bool crenellated)
        {
            if (!crenellated)
            {
                mb.Box(from, to + new Vector3(0, ParapetHeight * 0.6f, 0), face.Lightened(0.05f));
                return;
            }
            mb.Box(from, to + new Vector3(0, ParapetHeight * 0.55f, 0), face.Lightened(0.05f));
            var span = to - from;
            bool spanX = Mathf.Abs(span.X) > Mathf.Abs(span.Z);
            float length = spanX ? Mathf.Abs(span.X) : Mathf.Abs(span.Z);
            int merlons = Mathf.Max(1, Mathf.RoundToInt(length / 0.25f));
            for (int i = 0; i < merlons; i += 2)
            {
                float a = i / (float)merlons, b = (i + 1) / (float)merlons;
                var m0 = from.Lerp(to, a);
                var m1 = from.Lerp(to, b);
                mb.Box(new Vector3(Mathf.Min(m0.X, m1.X) - (spanX ? 0 : ParapetThickness / 2), top + ParapetHeight * 0.55f,
                        Mathf.Min(m0.Z, m1.Z) - (spanX ? ParapetThickness / 2 : 0)),
                    new Vector3(Mathf.Max(m0.X, m1.X) + (spanX ? 0 : ParapetThickness / 2), top + ParapetHeight,
                        Mathf.Max(m0.Z, m1.Z) + (spanX ? ParapetThickness / 2 : 0)), DarkStone);
            }
        }
        float x0 = (w ? x : cx - t), x1 = (e ? x + 1f : cx + t), z0 = (n ? y : cz - t), z1 = (s ? y + 1f : cz + t);
        float p = ParapetThickness;
        if (alongX)
        {
            Run(new Vector3(x0, top, cz - t), new Vector3(x1, top, cz - t + p), crenellated: inside != -1);
            Run(new Vector3(x0, top, cz + t - p), new Vector3(x1, top, cz + t), crenellated: inside != 1);
        }
        if (n || s || !alongX)
        {
            int sideInside = InsideSide(x, y, alongX: false);
            Run(new Vector3(cx - t, top, z0), new Vector3(cx - t + p, top, z1), crenellated: sideInside != -1);
            Run(new Vector3(cx + t - p, top, z0), new Vector3(cx + t, top, z1), crenellated: sideInside != 1);
        }

    }

    private static readonly Color Roof = new(0.36f, 0.38f, 0.44f);
    private static readonly Color Slit = new(0.12f, 0.12f, 0.14f);

    /// <summary>Battlements around the top edge of a square block.</summary>
    private static void Crown(MeshBuilder mb, float x0, float z0, float x1, float z1, float top, float size = 0.18f)
    {
        for (float px = x0; px < x1 - 0.01f; px += size * 2)
        {
            mb.Box(new Vector3(px, top, z0), new Vector3(Mathf.Min(px + size, x1), top + size, z0 + size), DarkStone);
            mb.Box(new Vector3(px, top, z1 - size), new Vector3(Mathf.Min(px + size, x1), top + size, z1), DarkStone);
        }
        for (float pz = z0 + size * 2; pz < z1 - size - 0.01f; pz += size * 2)
        {
            mb.Box(new Vector3(x0, top, pz), new Vector3(x0 + size, top + size, Mathf.Min(pz + size, z1)), DarkStone);
            mb.Box(new Vector3(x1 - size, top, pz), new Vector3(x1, top + size, Mathf.Min(pz + size, z1)), DarkStone);
        }
    }

    /// <summary>Arrow slits on the four faces of a block.</summary>
    private static void Slits(MeshBuilder mb, float x0, float z0, float x1, float z1, float y0, float y1)
    {
        float mx = (x0 + x1) / 2, mz = (z0 + z1) / 2;
        for (float y = y0; y + 0.25f <= y1; y += 0.7f)
        {
            mb.Box(new Vector3(mx - 0.03f, y, z0 - 0.01f), new Vector3(mx + 0.03f, y + 0.22f, z0), Slit);
            mb.Box(new Vector3(mx - 0.03f, y, z1), new Vector3(mx + 0.03f, y + 0.22f, z1 + 0.01f), Slit);
            mb.Box(new Vector3(x0 - 0.01f, y, mz - 0.03f), new Vector3(x0, y + 0.22f, mz + 0.03f), Slit);
            mb.Box(new Vector3(x1, y, mz - 0.03f), new Vector3(x1 + 0.01f, y + 0.22f, mz + 0.03f), Slit);
        }
    }

    /// <summary>A wooden watchtower over a palisade: four posts, a railed platform and a small roof.</summary>
    private static void Watchtower(MeshBuilder mb, int x, int y, float g)
    {
        float top = g + 1.5f;
        foreach (float px in new[] { x + 0.1f, x + 0.8f })
        foreach (float pz in new[] { y + 0.1f, y + 0.8f })
            mb.Box(new Vector3(px, g, pz), new Vector3(px + 0.1f, top + 0.5f, pz + 0.1f), Stake);
        mb.Box(new Vector3(x - 0.05f, top, y - 0.05f), new Vector3(x + 1.05f, top + 0.08f, y + 1.05f), Stake.Lightened(0.08f));
        // Railing, braces and a ladder.
        mb.Box(new Vector3(x - 0.05f, top + 0.08f, y - 0.05f), new Vector3(x + 1.05f, top + 0.3f, y + 0.02f), Stake.Darkened(0.1f));
        mb.Box(new Vector3(x - 0.05f, top + 0.08f, y + 0.98f), new Vector3(x + 1.05f, top + 0.3f, y + 1.05f), Stake.Darkened(0.1f));
        mb.Box(new Vector3(x - 0.05f, top + 0.08f, y - 0.05f), new Vector3(x + 0.02f, top + 0.3f, y + 1.05f), Stake.Darkened(0.1f));
        mb.Box(new Vector3(x + 0.98f, top + 0.08f, y - 0.05f), new Vector3(x + 1.05f, top + 0.3f, y + 1.05f), Stake.Darkened(0.1f));
        mb.Box(new Vector3(x + 0.1f, g + 0.6f, y + 0.12f), new Vector3(x + 0.9f, g + 0.68f, y + 0.18f), Stake.Darkened(0.2f));
        mb.GableRoof(new Vector3(x - 0.1f, top + 0.5f, y - 0.1f), new Vector3(x + 1.1f, top + 0.5f, y + 1.1f), 0.45f, Roof.Lightened(0.15f));
    }

    /// <summary>A stone tower rising above the wall-walk: battlements, arrow slits, and on a great tower a capped turret.</summary>
    private static void StoneTower(MeshBuilder mb, int x, int y, float g, byte tier)
    {
        bool great = tier >= 2;
        float grow = great ? 0.2f : 0.1f;
        float x0 = x - grow, x1 = x + 1 + grow, z0 = y - grow, z1 = y + 1 + grow;
        float top = g + StoneHeight(tier) + (great ? 1.4f : 1.0f);
        var face = WallStone.Darkened(great ? 0.22f : 0.16f);
        mb.Box(new Vector3(x0, g, z0), new Vector3(x1, top, z1), face, WallStone.Lightened(0.06f));
        // A string course where the tower rises above the walk.
        mb.Box(new Vector3(x0 - 0.04f, g + StoneHeight(tier), z0 - 0.04f), new Vector3(x1 + 0.04f, g + StoneHeight(tier) + 0.08f, z1 + 0.04f), DarkStone);
        Slits(mb, x0, z0, x1, z1, g + 0.5f, top - 0.3f);
        Crown(mb, x0, z0, x1, z1, top, great ? 0.2f : 0.17f);
        if (great)
        {
            // A corner turret with a slate cap.
            mb.Box(new Vector3(x1 - 0.45f, top, z0), new Vector3(x1, top + 0.8f, z0 + 0.45f), face.Lightened(0.03f));
            mb.Cone(new Vector3(x1 - 0.225f, top + 0.8f, z0 + 0.225f), 0.34f, 0.6f, 6, Roof);
        }
    }

    /// <summary>
    /// A gatehouse: two towers flanking the passage, joined above it so the walk crosses over, with a portcullis
    /// in the arch. In timber over a palisade.
    /// </summary>
    private static void Gatehouse(MeshBuilder mb, int x, int y, float g, byte tier, bool alongX)
    {
        bool timber = tier == 0;
        float h = timber ? 1.5f : StoneHeight(tier) + 0.7f;
        float depth = timber ? 0.45f : StoneHalfWidth(tier) + 0.3f; // how far the towers stand out from the wall line
        float open = 0.3f;
        var face = timber ? Stake : WallStone.Darkened(0.2f);
        var top = timber ? Stake.Lightened(0.1f) : WallStone.Lightened(0.06f);
        float cx = x + 0.5f, cz = y + 0.5f;
        // The two flanking towers and the bridge over the passage, laid out along the wall's direction.
        Vector3 P(float along, float height, float across) => alongX ? new Vector3(x + along, g + height, cz + across) : new Vector3(cx + across, g + height, y + along);
        void Block(float a0, float a1, float h0, float h1, float c0, float c1, Color c, Color? t = null)
        {
            var p0 = P(a0, h0, c0);
            var p1 = P(a1, h1, c1);
            mb.Box(new Vector3(Mathf.Min(p0.X, p1.X), p0.Y, Mathf.Min(p0.Z, p1.Z)), new Vector3(Mathf.Max(p0.X, p1.X), p1.Y, Mathf.Max(p0.Z, p1.Z)), c, t);
        }
        Block(-0.2f, 0.5f - open, 0, h, -depth, depth, face, top);
        Block(0.5f + open, 1.2f, 0, h, -depth, depth, face, top);
        Block(0.5f - open, 0.5f + open, h - (timber ? 0.45f : 0.7f), h - 0.15f, -depth, depth, face, top);
        // The portcullis (or timber doors) in the passage.
        for (float a = 0.5f - open + 0.06f; a < 0.5f + open - 0.02f; a += 0.1f)
            Block(a, a + 0.03f, 0.25f, h - (timber ? 0.45f : 0.7f), -0.02f, 0.02f, Slit);
        if (timber)
        {
            foreach (float a in new[] { -0.2f, 0.85f })
                mb.GableRoof(P(a, h, -depth - 0.05f), P(a + 0.35f, h, depth + 0.05f), 0.35f, Roof.Lightened(0.15f));
            return;
        }
        // Battlements on each tower and over the arch.
        foreach (var (a0, a1) in new[] { (-0.2f, 0.5f - open), (0.5f + open, 1.2f) })
        {
            var p0 = P(a0, h, -depth);
            var p1 = P(a1, h, depth);
            Crown(mb, Mathf.Min(p0.X, p1.X), Mathf.Min(p0.Z, p1.Z), Mathf.Max(p0.X, p1.X), Mathf.Max(p0.Z, p1.Z), g + h);
            Slits(mb, Mathf.Min(p0.X, p1.X), Mathf.Min(p0.Z, p1.Z), Mathf.Max(p0.X, p1.X), Mathf.Max(p0.Z, p1.Z), g + 0.5f, g + h - 0.4f);
        }
    }

    /// <summary>A flight of steps up the inside face of the wall to the wall-walk.</summary>
    private void Stairs(MeshBuilder mb, int x, int y, float g, byte tier, bool alongX)
    {
        int inside = InsideSide(x, y, alongX);
        if (inside == 0) return;
        int ix = alongX ? x : x + inside, iy = alongX ? y + inside : y;
        if (_map.GetTile(ix, iy) is { BuildingId: not 0 } || _settlement.Walls.ContainsKey((ix, iy))) return; // no room
        float h = StoneHeight(tier), t = StoneHalfWidth(tier);
        const int steps = 6;
        float depth = 0.32f;
        for (int i = 0; i < steps; i++)
        {
            float rise = h * (i + 1) / steps;
            float a = i / (float)steps, b = (i + 1) / (float)steps;
            if (alongX)
            {
                float z0 = inside > 0 ? y + 0.5f + t : y + 0.5f - t - depth;
                mb.Box(new Vector3(x + a, g, z0), new Vector3(x + b, g + rise, z0 + depth), WallStone.Darkened(0.12f), WallStone.Lightened(0.05f));
            }
            else
            {
                float x0 = inside > 0 ? x + 0.5f + t : x + 0.5f - t - depth;
                mb.Box(new Vector3(x0, g, y + a), new Vector3(x0 + depth, g + rise, y + b), WallStone.Darkened(0.12f), WallStone.Lightened(0.05f));
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
