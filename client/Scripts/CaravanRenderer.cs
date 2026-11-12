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

    public override void _Process(double delta)
    {
        if (_world == null) return;
        long tick = _world.Time.Tick;
        int n = 0;
        foreach (var c in _world.Trade.Caravans)
        {
            if (n >= MaxCaravans) break;
            float t = (float)c.Progress(tick);
            var from = new Vector2(c.StartX + 0.5f, c.StartY + 0.5f);
            var to = new Vector2(c.EndX + 0.5f, c.EndY + 0.5f);
            var p = from.Lerp(to, t);
            float y = Mathf.Max(TerrainShape.HeightAt(_world.Map, p.X, p.Y), TerrainShape.WaterLevel);
            var dir = (to - from).Normalized();
            float yaw = dir == Vector2.Zero ? 0 : Mathf.Atan2(dir.X, dir.Y);
            _carts.SetInstanceTransform(n, new Transform3D(new Basis(Vector3.Up, yaw), new Vector3(p.X, y, p.Y)));
            _carts.SetInstanceColor(n, c.Kind == CaravanKind.Delivery ? TownCart : MerchantCart);
            n++;
        }
        _carts.VisibleInstanceCount = n;
    }
}
