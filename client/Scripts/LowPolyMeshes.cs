using Godot;
using Sim.Core.Data;

namespace MedievalClient;

/// <summary>Procedural placeholder art. Meshes are cached and shared between instances.</summary>
public static class LowPolyMeshes
{
    private static readonly Dictionary<string, ArrayMesh> BuildingCache = new();
    private static readonly Dictionary<(int, int), ArrayMesh> ScaffoldCache = new();
    private static readonly Dictionary<(int, int), ArrayMesh> PlotCache = new();

    public static readonly Color Wattle = new(0.86f, 0.79f, 0.62f);
    public static readonly Color Timber = new(0.36f, 0.25f, 0.16f);
    public static readonly Color Thatch = new(0.80f, 0.64f, 0.32f);
    public static readonly Color Stone = new(0.70f, 0.68f, 0.63f);
    public static readonly Color Tiles = new(0.58f, 0.30f, 0.20f);
    public static readonly Color Wood = new(0.55f, 0.38f, 0.22f);
    public static readonly Color Soil = new(0.45f, 0.33f, 0.20f);
    public static readonly Color Wheat = new(0.88f, 0.76f, 0.32f);

    /// <summary>Model in local space: origin at the footprint's top-left corner, 1 unit per tile.</summary>
    public static ArrayMesh Building(BuildingDef def)
    {
        if (BuildingCache.TryGetValue(def.Id, out var cached)) return cached;
        var mb = new MeshBuilder { Center = new Vector3(def.Width / 2f, 0.4f, def.Height / 2f) };
        float w = def.Width, h = def.Height;

        switch (def.Id)
        {
            case "hovel":
                House(mb, new Vector3(0.2f, 0, 0.25f), new Vector3(w - 0.2f, 0.75f, h - 0.25f), Wattle, Thatch, 0.6f);
                break;

            case "woodcutter":
                House(mb, new Vector3(0.15f, 0, 0.15f), new Vector3(w - 0.55f, 0.6f, h - 0.6f), Wattle, Thatch, 0.45f);
                for (int i = 0; i < 3; i++) // log pile
                    mb.Box(new Vector3(0.2f, i * 0.12f, h - 0.45f + i * 0.04f), new Vector3(w - 0.2f, i * 0.12f + 0.12f, h - 0.33f + i * 0.04f), Wood);
                break;

            case "stockpile":
                mb.Box(new Vector3(0.1f, 0, 0.1f), new Vector3(w - 0.1f, 0.06f, h - 0.1f), Wood.Lightened(0.15f));
                mb.Box(new Vector3(0.3f, 0.06f, 0.3f), new Vector3(0.8f, 0.5f, 0.8f), Wood);
                mb.Box(new Vector3(0.9f, 0.06f, 0.3f), new Vector3(1.4f, 0.4f, 0.75f), Wood.Darkened(0.1f));
                mb.Box(new Vector3(1.7f, 0.06f, 1.5f), new Vector3(2.6f, 0.35f, 2.6f), Stone);
                mb.Box(new Vector3(0.35f, 0.06f, 1.6f), new Vector3(1.2f, 0.3f, 2.5f), Wheat.Darkened(0.2f));
                mb.Box(new Vector3(0.35f, 0.5f, 0.35f), new Vector3(0.75f, 0.8f, 0.75f), Wood.Lightened(0.1f));
                break;

            case "farm":
                mb.Box(new Vector3(0.05f, 0, 0.05f), new Vector3(w - 0.05f, 0.04f, h - 0.05f), Soil);
                for (float z = 0.3f; z < h - 0.2f; z += 0.5f)
                    mb.Box(new Vector3(0.2f, 0.04f, z), new Vector3(w - 0.2f, 0.28f, z + 0.22f), Wheat);
                break;

            case "quarry":
                mb.Box(new Vector3(0.1f, 0, 0.1f), new Vector3(w - 0.1f, 0.05f, h - 0.1f), Stone.Darkened(0.25f));
                mb.Box(new Vector3(0.3f, 0.05f, 0.3f), new Vector3(1.0f, 0.4f, 1.0f), Stone);
                mb.Box(new Vector3(1.2f, 0.05f, 0.4f), new Vector3(1.8f, 0.3f, 0.9f), Stone.Lightened(0.1f));
                mb.Box(new Vector3(0.4f, 0.4f, 0.4f), new Vector3(0.9f, 0.65f, 0.9f), Stone.Darkened(0.05f));
                mb.Box(new Vector3(2.2f, 0, 2.2f), new Vector3(2.32f, 1.4f, 2.32f), Timber); // crane mast
                mb.Box(new Vector3(1.3f, 1.3f, 2.2f), new Vector3(2.32f, 1.4f, 2.32f), Timber); // crane arm
                break;

            case "bakery":
                House(mb, new Vector3(0.2f, 0, 0.3f), new Vector3(w - 0.2f, 0.95f, h - 0.3f), Stone, Tiles, 0.7f);
                mb.Box(new Vector3(w - 0.75f, 0.9f, 0.6f), new Vector3(w - 0.45f, 1.85f, 0.9f), Stone.Darkened(0.15f)); // chimney
                break;

            default:
                House(mb, new Vector3(0.1f, 0, 0.1f), new Vector3(w - 0.1f, 0.8f, h - 0.1f), Stone, Tiles, 0.6f);
                break;
        }

        var mesh = mb.Commit();
        BuildingCache[def.Id] = mesh;
        return mesh;
    }

    private static void House(MeshBuilder mb, Vector3 min, Vector3 max, Color walls, Color roof, float roofHeight)
    {
        mb.Box(min, max, walls);
        // Timber corner posts give the half-timbered look.
        const float t = 0.06f;
        foreach (var (x, z) in new[] { (min.X, min.Z), (max.X - t, min.Z), (min.X, max.Z - t), (max.X - t, max.Z - t) })
            mb.Box(new Vector3(x - 0.01f, 0, z - 0.01f), new Vector3(x + t + 0.01f, max.Y, z + t + 0.01f), Timber);
        // Door on the south (+Z) face.
        float cx = (min.X + max.X) / 2;
        mb.Box(new Vector3(cx - 0.12f, 0, max.Z), new Vector3(cx + 0.12f, 0.4f, max.Z + 0.02f), Timber.Darkened(0.3f));
        mb.GableRoof(new Vector3(min.X - 0.1f, max.Y, min.Z - 0.1f), new Vector3(max.X + 0.1f, max.Y, max.Z + 0.1f), roofHeight, roof);
    }

    public static ArrayMesh Scaffold(int width, int height)
    {
        if (ScaffoldCache.TryGetValue((width, height), out var cached)) return cached;
        var mb = new MeshBuilder { Center = new Vector3(width / 2f, 0.6f, height / 2f) };
        const float t = 0.05f, top = 1.2f;
        var color = Wood.Lightened(0.2f);
        foreach (float x in new[] { 0.1f, width - 0.1f - t })
        foreach (float z in new[] { 0.1f, height - 0.1f - t })
            mb.Box(new Vector3(x, 0, z), new Vector3(x + t, top, z + t), color);
        foreach (float y in new[] { 0.4f, 0.8f, top - t })
        {
            mb.Box(new Vector3(0.1f, y, 0.1f), new Vector3(width - 0.1f, y + t, 0.1f + t), color);
            mb.Box(new Vector3(0.1f, y, height - 0.1f - t), new Vector3(width - 0.1f, y + t, height - 0.1f), color);
            mb.Box(new Vector3(0.1f, y, 0.1f), new Vector3(0.1f + t, y + t, height - 0.1f), color);
            mb.Box(new Vector3(width - 0.1f - t, y, 0.1f), new Vector3(width - 0.1f, y + t, height - 0.1f), color);
        }
        var mesh = mb.Commit();
        ScaffoldCache[(width, height)] = mesh;
        return mesh;
    }

    /// <summary>Marked-out plot for a building that is still waiting for materials.</summary>
    public static ArrayMesh Plot(int width, int height)
    {
        if (PlotCache.TryGetValue((width, height), out var cached)) return cached;
        var mb = new MeshBuilder { Center = new Vector3(width / 2f, 0, height / 2f) };
        var color = new Color(0.9f, 0.85f, 0.7f);
        const float t = 0.07f, y = 0.03f;
        mb.Box(new Vector3(0, 0, 0), new Vector3(width, y, t), color);
        mb.Box(new Vector3(0, 0, height - t), new Vector3(width, y, height), color);
        mb.Box(new Vector3(0, 0, 0), new Vector3(t, y, height), color);
        mb.Box(new Vector3(width - t, 0, 0), new Vector3(width, y, height), color);
        foreach (float x in new[] { 0f, width - 0.12f })
        foreach (float z in new[] { 0f, height - 0.12f })
            mb.Box(new Vector3(x, 0, z), new Vector3(x + 0.12f, 0.3f, z + 0.12f), Wood);
        var mesh = mb.Commit();
        PlotCache[(width, height)] = mesh;
        return mesh;
    }

    public static readonly ArrayMesh Tree = BuildTree();
    public static readonly ArrayMesh Rock = BuildRock();
    public static readonly ArrayMesh CitizenBody = BuildCitizenBody();
    public static readonly ArrayMesh CitizenHead = BuildCitizenHead();
    public static readonly ArrayMesh SelectionMarker = BuildMarker();

    private static ArrayMesh BuildTree()
    {
        var mb = new MeshBuilder { Center = new Vector3(0, 0.6f, 0) };
        mb.Box(new Vector3(-0.06f, 0, -0.06f), new Vector3(0.06f, 0.35f, 0.06f), Timber);
        var leaves = new Color(0.20f, 0.42f, 0.18f);
        mb.Cone(new Vector3(0, 0.25f, 0), 0.42f, 0.7f, 6, leaves);
        mb.Cone(new Vector3(0, 0.6f, 0), 0.32f, 0.6f, 6, leaves.Lightened(0.08f));
        return mb.Commit();
    }

    private static ArrayMesh BuildRock()
    {
        var mb = new MeshBuilder { Center = new Vector3(0, 0.1f, 0) };
        var c = new Color(0.62f, 0.60f, 0.57f);
        mb.Cone(new Vector3(0, 0, 0), 0.32f, 0.35f, 5, c);
        mb.Cone(new Vector3(0.2f, 0, 0.12f), 0.18f, 0.22f, 4, c.Darkened(0.1f));
        return mb.Commit();
    }

    private static ArrayMesh BuildCitizenBody()
    {
        // White so the MultiMesh instance colour (job colour) tints it.
        var mb = new MeshBuilder { Center = new Vector3(0, 0.15f, 0) };
        mb.Box(new Vector3(-0.08f, 0, -0.05f), new Vector3(0.08f, 0.28f, 0.05f), Colors.White);
        return mb.Commit();
    }

    private static ArrayMesh BuildCitizenHead()
    {
        var mb = new MeshBuilder { Center = new Vector3(0, 0.34f, 0) };
        mb.Box(new Vector3(-0.055f, 0.28f, -0.055f), new Vector3(0.055f, 0.39f, 0.055f), Colors.White);
        return mb.Commit();
    }

    private static ArrayMesh BuildMarker()
    {
        var mb = new MeshBuilder();
        var c = new Color(1f, 0.85f, 0.2f);
        var top = new Vector3(0, 0.25f, 0);
        var bottom = Vector3.Zero;
        for (int i = 0; i < 4; i++)
        {
            float a0 = Mathf.Tau * i / 4, a1 = Mathf.Tau * (i + 1) / 4;
            var p0 = new Vector3(Mathf.Cos(a0) * 0.1f, 0.12f, Mathf.Sin(a0) * 0.1f);
            var p1 = new Vector3(Mathf.Cos(a1) * 0.1f, 0.12f, Mathf.Sin(a1) * 0.1f);
            mb.Center = new Vector3(0, 0.12f, 0);
            mb.Tri(p0, p1, top, c);
            mb.Tri(p0, p1, bottom, c.Darkened(0.2f));
        }
        return mb.Commit();
    }
}
