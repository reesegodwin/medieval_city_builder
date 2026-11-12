using Godot;
using Sim.Core.Simulation;
using Sim.Core.Trade;

namespace MedievalClient;

/// <summary>
/// Draws every caravan on the road as an ox cart moving between towns (merchants in their own colours). Caravans
/// are simulation state; this only shows them, placed by their travel progress on the ground's shape.
/// </summary>
public partial class CaravanRenderer : Node3D
{
    private const int MaxCaravans = 256;
    private static readonly Color TownCart = Colors.White;
    private static readonly Color MerchantCart = new(1f, 0.75f, 0.55f);

    private GameWorld _world = null!;
    private MultiMesh _carts = null!;

    public void Initialize(GameWorld world)
    {
        _world = world;
        _carts = TerrainRenderer.NewMultiMesh(LowPolyMeshes.Caravan, MaxCaravans, colors: true);
        _carts.VisibleInstanceCount = 0;
        AddChild(new MultiMeshInstance3D { Multimesh = _carts });
    }

    /// <summary>Point and heading at a share of the way along the caravan's path (roads, plus a merchant's first leg).</summary>
    public static (Vector2 Point, Vector2 Direction) PositionAlongPath(Caravan c, float t)
    {
        var points = Trading.Points(c);
        if (points.Count < 2)
        {
            var from = new Vector2(c.StartX + 0.5f, c.StartY + 0.5f);
            var to = new Vector2(c.EndX + 0.5f, c.EndY + 0.5f);
            return (from.Lerp(to, t), (to - from).Normalized());
        }
        float total = 0;
        for (int i = 1; i < points.Count; i++) total += Distance(points[i - 1], points[i]);
        float target = t * total;
        for (int i = 1; i < points.Count; i++)
        {
            float leg = Distance(points[i - 1], points[i]);
            var a = new Vector2(points[i - 1].X + 0.5f, points[i - 1].Y + 0.5f);
            var b = new Vector2(points[i].X + 0.5f, points[i].Y + 0.5f);
            if (target <= leg || i == points.Count - 1)
                return (a.Lerp(b, leg <= 0 ? 1 : Mathf.Clamp(target / leg, 0, 1)), (b - a).Normalized());
            target -= leg;
        }
        return (new Vector2(c.EndX + 0.5f, c.EndY + 0.5f), Vector2.Zero);
    }

    private static float Distance((int X, int Y) a, (int X, int Y) b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    public override void _Process(double delta)
    {
        if (_world == null) return;
        long tick = _world.Time.Tick;
        int n = 0;
        foreach (var c in _world.Trade.Caravans)
        {
            if (n >= MaxCaravans) break;
            var (p, dir) = PositionAlongPath(c, (float)c.Progress(tick));
            float y = Mathf.Max(TerrainShape.HeightAt(_world.Map, p.X, p.Y), TerrainShape.WaterLevel);
            float yaw = dir == Vector2.Zero ? 0 : Mathf.Atan2(dir.X, dir.Y);
            _carts.SetInstanceTransform(n, new Transform3D(new Basis(Vector3.Up, yaw), new Vector3(p.X, y, p.Y)));
            _carts.SetInstanceColor(n, c.Kind == CaravanKind.Delivery ? TownCart : MerchantCart);
            n++;
        }
        _carts.VisibleInstanceCount = n;
    }
}
