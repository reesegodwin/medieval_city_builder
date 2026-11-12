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

    private static readonly StandardMaterial3D[] Tints =
    {
        Materials.Tinted(new Color(1f, 1f, 1f)),
        Materials.Tinted(new Color(0.94f, 0.92f, 0.88f)),
        Materials.Tinted(new Color(1f, 0.97f, 0.9f)),
        Materials.Tinted(new Color(0.9f, 0.9f, 0.92f)),
    };

    private readonly Dictionary<int, View> _views = new();
    private readonly HashSet<int> _seen = new();
    private Settlement _settlement = null!;

    public void Initialize(Settlement settlement) => _settlement = settlement;

    public override void _Process(double delta)
    {
        if (_settlement == null) return;
        _seen.Clear();
        foreach (var b in _settlement.Buildings)
        {
            if (!_views.TryGetValue(b.Id, out var view))
            {
                view = CreateView(b);
                _views[b.Id] = view;
            }
            Sync(b, view);
            _seen.Add(b.Id);
        }

        // Demolished buildings.
        if (_views.Count != _seen.Count)
        {
            foreach (int id in _views.Keys.Where(id => !_seen.Contains(id)).ToList())
            {
                _views[id].Root.QueueFree();
                _views.Remove(id);
            }
        }
    }

    private View CreateView(Building b)
    {
        var root = new Node3D { Name = $"{b.Def.Id}_{b.Id}", Position = Coords.TileCorner(b.X, b.Y) };
        var view = new View
        {
            Root = root,
            Model = new MeshInstance3D { Mesh = LowPolyMeshes.Building(b.Def), MaterialOverride = Tints[b.Id % Tints.Length] },
            Scaffold = new MeshInstance3D { Mesh = LowPolyMeshes.Scaffold(b.Def.Width, b.Def.Height) },
            Plot = new MeshInstance3D { Mesh = LowPolyMeshes.Plot(b.Def.Width, b.Def.Height) },
        };
        // An earthen footing under every building, so it never floats where the ground slopes towards water.
        root.AddChild(new MeshInstance3D { Mesh = LowPolyMeshes.Foundation(b.Def.Width, b.Def.Height) });
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
