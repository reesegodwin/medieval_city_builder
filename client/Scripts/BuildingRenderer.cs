using Godot;
using Sim.Core.Settlements;

namespace MedievalClient;

/// <summary>
/// Mirrors the settlement's buildings: a plot marker while waiting for materials, a scaffold with
/// the building rising inside it during construction, and the finished model once operational.
/// </summary>
public partial class BuildingRenderer : Node3D
{
    private sealed class View
    {
        public required Node3D Root;
        public required MeshInstance3D Model;
        public required MeshInstance3D Scaffold;
        public required MeshInstance3D Plot;
    }

    private readonly Dictionary<int, View> _views = new();
    private Settlement _settlement = null!;

    public void Initialize(Settlement settlement) => _settlement = settlement;

    public override void _Process(double delta)
    {
        if (_settlement == null) return;
        foreach (var b in _settlement.Buildings)
        {
            if (!_views.TryGetValue(b.Id, out var view))
            {
                view = CreateView(b);
                _views[b.Id] = view;
            }
            Sync(b, view);
        }
    }

    private View CreateView(Building b)
    {
        var root = new Node3D { Name = $"{b.Def.Id}_{b.Id}", Position = Coords.TileCorner(b.X, b.Y) };
        var view = new View
        {
            Root = root,
            Model = new MeshInstance3D { Mesh = LowPolyMeshes.Building(b.Def) },
            Scaffold = new MeshInstance3D { Mesh = LowPolyMeshes.Scaffold(b.Def.Width, b.Def.Height) },
            Plot = new MeshInstance3D { Mesh = LowPolyMeshes.Plot(b.Def.Width, b.Def.Height) },
        };
        root.AddChild(view.Model);
        root.AddChild(view.Scaffold);
        root.AddChild(view.Plot);
        AddChild(root);
        return view;
    }

    private static void Sync(Building b, View view)
    {
        switch (b.State)
        {
            case BuildingState.AwaitingMaterials:
                view.Model.Visible = false;
                view.Scaffold.Visible = false;
                view.Plot.Visible = true;
                break;

            case BuildingState.UnderConstruction:
                float progress = b.Def.BuildDays > 0 ? (float)(b.ConstructionProgress / b.Def.BuildDays) : 1f;
                view.Model.Visible = true;
                view.Model.Scale = new Vector3(1, Mathf.Clamp(progress, 0.05f, 1f), 1);
                view.Scaffold.Visible = true;
                view.Plot.Visible = false;
                break;

            default:
                view.Model.Visible = true;
                view.Model.Scale = Vector3.One;
                view.Scaffold.Visible = false;
                view.Plot.Visible = false;
                break;
        }
    }
}
