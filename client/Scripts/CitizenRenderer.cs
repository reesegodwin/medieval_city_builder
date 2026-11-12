using Godot;
using Sim.Core.Citizens;
using Sim.Core.Settlements;
using Sim.Core.Systems;
using Sim.Core.Util;

namespace MedievalClient;

/// <summary>
/// Draws every townsperson with two MultiMeshes (bodies tinted by job, heads by skin tone).
/// Walking is purely cosmetic: the simulation only knows each citizen's home, job and the
/// nearest stockpile, and the client animates them looping between those places.
/// </summary>
public partial class CitizenRenderer : Node3D
{
    public const float WalkSpeed = 1.3f; // tiles per real second
    public const float FigureScale = 1.6f;

    private sealed class Route
    {
        public required Vector3[] Points;
        public required float[] Dwell;
        public required float Phase;
        public int JobId, HomeId;
        public float Length;
    }

    private static readonly Color[] SkinTones =
    {
        new(0.96f, 0.80f, 0.66f), new(0.88f, 0.70f, 0.55f), new(0.76f, 0.57f, 0.42f), new(0.55f, 0.39f, 0.27f),
    };

    private readonly Dictionary<int, Route> _routes = new();
    private Settlement _settlement = null!;
    private MultiMesh _bodies = null!;
    private MultiMesh _heads = null!;
    private float _clock;

    /// <summary>Current drawn position of each citizen, for mouse picking.</summary>
    public Dictionary<int, Vector3> Positions { get; } = new();

    public bool Animate { get; set; } = true;

    public void Initialize(Settlement settlement)
    {
        _settlement = settlement;
        _bodies = TerrainRenderer.NewMultiMesh(LowPolyMeshes.CitizenBody, 64, colors: true);
        _heads = TerrainRenderer.NewMultiMesh(LowPolyMeshes.CitizenHead, 64, colors: true);
        AddChild(new MultiMeshInstance3D { Multimesh = _bodies, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        AddChild(new MultiMeshInstance3D { Multimesh = _heads, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    public static Color JobColor(Settlement settlement, Citizen c)
    {
        if (!c.IsAdult) return new Color(0.85f, 0.85f, 0.75f);
        if (c.JobBuildingId is not int jobId || settlement.FindBuilding(jobId) is not { } job)
            return new Color(0.45f, 0.5f, 0.6f);
        return job.CurrentJobKind switch
        {
            "farmer" => new Color(0.75f, 0.62f, 0.30f),
            "baker" => new Color(0.95f, 0.93f, 0.88f),
            "woodcutter" => new Color(0.30f, 0.50f, 0.25f),
            "quarrier" => new Color(0.55f, 0.55f, 0.58f),
            JobKinds.Builder => new Color(0.85f, 0.45f, 0.15f),
            _ => new Color(0.6f, 0.3f, 0.3f),
        };
    }

    public override void _Process(double delta)
    {
        if (_settlement == null) return;
        if (Animate) _clock += (float)delta;

        var citizens = _settlement.Citizens;
        EnsureCapacity(citizens.Count);
        Positions.Clear();

        for (int i = 0; i < citizens.Count; i++)
        {
            var c = citizens[i];
            var route = GetRoute(c);
            var (pos, dir) = Evaluate(route, _clock + route.Phase);
            pos.Y = 0.02f + Mathf.Abs(Mathf.Sin((_clock + route.Phase) * 9f)) * 0.025f * dir.Length();

            float scale = (c.IsAdult ? 1f : 0.7f) * FigureScale;
            float yaw = dir.LengthSquared() > 0.0001f ? Mathf.Atan2(dir.X, dir.Z) : route.Phase;
            var xform = new Transform3D(new Basis(Vector3.Up, yaw).Scaled(Vector3.One * scale), pos);

            _bodies.SetInstanceTransform(i, xform);
            _bodies.SetInstanceColor(i, JobColor(_settlement, c));
            _heads.SetInstanceTransform(i, xform);
            _heads.SetInstanceColor(i, SkinTones[c.Id % SkinTones.Length]);
            Positions[c.Id] = pos;
        }
        _bodies.VisibleInstanceCount = citizens.Count;
        _heads.VisibleInstanceCount = citizens.Count;
    }

    private void EnsureCapacity(int count)
    {
        if (_bodies.InstanceCount >= count) return;
        int capacity = _bodies.InstanceCount;
        while (capacity < count) capacity *= 2;
        _bodies.InstanceCount = capacity;
        _heads.InstanceCount = capacity;
    }

    private Route GetRoute(Citizen c)
    {
        int jobId = c.JobBuildingId ?? 0, homeId = c.HomeBuildingId ?? 0;
        if (_routes.TryGetValue(c.Id, out var route) && route.JobId == jobId && route.HomeId == homeId)
            return route;

        route = BuildRoute(c, jobId, homeId);
        _routes[c.Id] = route;
        return route;
    }

    private Route BuildRoute(Citizen c, int jobId, int homeId)
    {
        ulong seed = (ulong)c.Id * 2654435761UL;
        float Rand(int salt) => (float)Hashing.Hash01(seed, salt, c.Id);

        var home = homeId != 0 ? _settlement.FindBuilding(homeId) : null;
        var job = jobId != 0 ? _settlement.FindBuilding(jobId) : null;
        Vector3 homePoint = home != null
            ? Coords.BuildingDoor(home)
            : new Vector3(_settlement.CenterX + Rand(1) * 6 - 3, 0, _settlement.CenterY + Rand(2) * 6 - 3);
        Vector3 Near(Vector3 p, float spread, int salt) =>
            p + new Vector3((Rand(salt) - 0.5f) * spread, 0, (Rand(salt + 1) - 0.5f) * spread);

        Vector3[] points;
        float[] dwell;
        if (job != null)
        {
            // Farmers work out in the field; everyone else around the workplace.
            Vector3 work = job.Def.Id == "farm" && job.IsOperational
                ? Coords.TileCorner(job.X, job.Y) + new Vector3(0.3f + Rand(3) * (job.Def.Width - 0.6f), 0, 0.3f + Rand(4) * (job.Def.Height - 0.6f))
                : Near(Coords.BuildingDoor(job), 0.8f, 5);
            var stock = Logistics.NearestStockpile(_settlement, job);
            Vector3 stockPoint = stock != null ? Near(Coords.BuildingDoor(stock), 1.2f, 7) : work;
            points = new[] { homePoint, work, stockPoint, Near(work, 0.6f, 9), stockPoint };
            dwell = new[] { 2f, 7f, 1.5f, 7f, 1.5f };
        }
        else
        {
            // Children and idle adults potter about near home.
            points = new[] { homePoint, Near(homePoint, 5f, 11), Near(homePoint, 5f, 13) };
            dwell = new[] { 3f, 4f, 4f };
        }

        var route = new Route { Points = points, Dwell = dwell, Phase = Rand(17) * 60f, JobId = jobId, HomeId = homeId };
        for (int i = 0; i < points.Length; i++)
            route.Length += points[i].DistanceTo(points[(i + 1) % points.Length]) / WalkSpeed + dwell[i];
        return route;
    }

    /// <summary>Position and walking direction along a looping route at time t (seconds).</summary>
    private static (Vector3 Position, Vector3 Direction) Evaluate(Route route, float t)
    {
        if (route.Length <= 0.001f) return (route.Points[0], Vector3.Zero);
        float remaining = t % route.Length;
        for (int i = 0; i < route.Points.Length; i++)
        {
            if (remaining < route.Dwell[i]) return (route.Points[i], Vector3.Zero);
            remaining -= route.Dwell[i];

            var from = route.Points[i];
            var to = route.Points[(i + 1) % route.Points.Length];
            float legTime = from.DistanceTo(to) / WalkSpeed;
            if (remaining < legTime)
            {
                var dir = (to - from).Normalized();
                return (from.Lerp(to, remaining / legTime), dir);
            }
            remaining -= legTime;
        }
        return (route.Points[0], Vector3.Zero);
    }
}
