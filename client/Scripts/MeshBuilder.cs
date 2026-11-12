using Godot;

namespace MedievalClient;

/// <summary>
/// Small mesh builder for flat-shaded, vertex-coloured low-poly meshes.
/// Normals are set explicitly (pointing away from <see cref="Center"/> unless given), and the
/// shared material disables back-face culling, so face winding never matters.
/// </summary>
public sealed class MeshBuilder
{
    private readonly List<Vector3> _vertices = new();
    private readonly List<Vector3> _normals = new();
    private readonly List<Color> _colors = new();

    /// <summary>Reference point used to orient normals outward for closed shapes.</summary>
    public Vector3 Center { get; set; }

    public bool IsEmpty => _vertices.Count == 0;

    public void Tri(Vector3 a, Vector3 b, Vector3 c, Color color, Vector3? normal = null)
    {
        Vector3 n = normal ?? OutwardNormal(a, b, c);
        // Godot treats clockwise triangles as front-facing.
        bool clockwise = (b - a).Cross(c - a).Dot(n) < 0;
        _vertices.Add(a);
        _vertices.Add(clockwise ? b : c);
        _vertices.Add(clockwise ? c : b);
        for (int i = 0; i < 3; i++)
        {
            _normals.Add(n);
            _colors.Add(color);
        }
    }

    /// <summary>Quad from four corners in order around the edge.</summary>
    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, Vector3? normal = null)
    {
        Vector3 n = normal ?? OutwardNormal(a, b, c);
        Tri(a, b, c, color, n);
        Tri(a, c, d, color, n);
    }

    /// <summary>Axis-aligned box. The bottom face is skipped because it is never seen.</summary>
    public void Box(Vector3 min, Vector3 max, Color color, Color? topColor = null)
    {
        Color top = topColor ?? color;
        Color side = color.Darkened(0.08f);
        var (x0, y0, z0) = (min.X, min.Y, min.Z);
        var (x1, y1, z1) = (max.X, max.Y, max.Z);

        Quad(new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1), top, Vector3.Up);
        Quad(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), color, Vector3.Back);
        Quad(new(x0, y0, z0), new(x1, y0, z0), new(x1, y1, z0), new(x0, y1, z0), side, Vector3.Forward);
        Quad(new(x1, y0, z0), new(x1, y0, z1), new(x1, y1, z1), new(x1, y1, z0), side, Vector3.Right);
        Quad(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0), color, Vector3.Left);
    }

    /// <summary>Gable roof over a rectangle; the ridge runs along the longer side.</summary>
    public void GableRoof(Vector3 min, Vector3 max, float ridgeHeight, Color color)
    {
        float y0 = min.Y, y1 = min.Y + ridgeHeight;
        var (x0, z0, x1, z1) = (min.X, min.Z, max.X, max.Z);
        Color shade = color.Darkened(0.15f);
        var saved = Center;
        Center = new Vector3((x0 + x1) / 2, y0, (z0 + z1) / 2);

        if (x1 - x0 >= z1 - z0)
        {
            float zm = (z0 + z1) / 2;
            Quad(new(x0, y0, z0), new(x1, y0, z0), new(x1, y1, zm), new(x0, y1, zm), shade);
            Quad(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, zm), new(x0, y1, zm), color);
            Tri(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, zm), color.Darkened(0.05f));
            Tri(new(x1, y0, z0), new(x1, y0, z1), new(x1, y1, zm), color.Darkened(0.05f));
        }
        else
        {
            float xm = (x0 + x1) / 2;
            Quad(new(x0, y0, z0), new(x0, y0, z1), new(xm, y1, z1), new(xm, y1, z0), shade);
            Quad(new(x1, y0, z0), new(x1, y0, z1), new(xm, y1, z1), new(xm, y1, z0), color);
            Tri(new(x0, y0, z0), new(x1, y0, z0), new(xm, y1, z0), color.Darkened(0.05f));
            Tri(new(x0, y0, z1), new(x1, y0, z1), new(xm, y1, z1), color);
        }
        Center = saved;
    }

    /// <summary>Low-poly cone (tree crowns, spires).</summary>
    public void Cone(Vector3 baseCenter, float radius, float height, int sides, Color color)
    {
        var saved = Center;
        Center = baseCenter + new Vector3(0, height * 0.3f, 0);
        Vector3 apex = baseCenter + new Vector3(0, height, 0);
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides;
            var p0 = baseCenter + new Vector3(Mathf.Cos(a0) * radius, 0, Mathf.Sin(a0) * radius);
            var p1 = baseCenter + new Vector3(Mathf.Cos(a1) * radius, 0, Mathf.Sin(a1) * radius);
            Tri(p0, p1, apex, i % 2 == 0 ? color : color.Darkened(0.1f));
            Tri(p0, p1, baseCenter, color.Darkened(0.3f), Vector3.Down);
        }
        Center = saved;
    }

    public ArrayMesh Commit(Material? material = null)
    {
        var mesh = new ArrayMesh();
        if (_vertices.Count == 0) return mesh;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material ?? Materials.VertexColored);
        return mesh;
    }

    private Vector3 OutwardNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 n = (b - a).Cross(c - a).Normalized();
        Vector3 centroid = (a + b + c) / 3f;
        return n.Dot(centroid - Center) < 0 ? -n : n;
    }
}

public static class Materials
{
    public static readonly StandardMaterial3D VertexColored = new()
    {
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        Roughness = 1.0f,
    };

    /// <summary>Vertex-coloured material tinted by a multiplier (subtle per-building colour variation).</summary>
    public static StandardMaterial3D Tinted(Color tint) => new()
    {
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        AlbedoColor = tint,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        Roughness = 1.0f,
    };

    /// <summary>Translucent, unshaded, coloured by vertex/instance colour (tool previews).</summary>
    public static readonly StandardMaterial3D Overlay = new()
    {
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D Water = new()
    {
        AlbedoColor = new Color(0.28f, 0.52f, 0.78f, 0.72f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        Roughness = 0.08f,
        Metallic = 0.25f,
        MetallicSpecular = 0.8f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static StandardMaterial3D Transparent(Color color) => new()
    {
        AlbedoColor = color,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = false,
    };
}
