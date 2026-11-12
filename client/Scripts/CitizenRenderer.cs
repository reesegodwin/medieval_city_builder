using Godot;
using Sim.Core.Citizens;
using Sim.Core.Settlements;
using Sim.Core.Systems;
using Sim.Core.Util;
using Sim.Core.World;

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
        public int JobId, HomeId, RoadVersion;
        public bool Pathed;
        public float Length;
    }

    private static readonly Color[] SkinTones =
    {
        new(0.96f, 0.80f, 0.66f), new(0.88f, 0.70f, 0.55f), new(0.76f, 0.57f, 0.42f), new(0.55f, 0.39f, 0.27f),
    };

    private readonly Dictionary<int, Route> _routes = new();
    private Settlement _settlement = null!;
    private Sim.Core.Simulation.GameWorld _world = null!;
    private MultiMesh _bodies = null!;
    private MultiMesh _heads = null!;
    private float _clock;

    /// <summary>Current drawn position of each citizen, for mouse picking.</summary>
    public Dictionary<int, Vector3> Positions { get; } = new();

    public bool Animate { get; set; } = true;

    public void Initialize(Sim.Core.Simulation.GameWorld world, Settlement settlement)
    {
        _world = world;
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
        _pathBudget = MaxPathedRoutesPerFrame;

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

    /// <summary>Pathfinding is spread over frames: at most this many routes are (re)built per frame.</summary>
    public const int MaxPathedRoutesPerFrame = 20;
    private const int MaxPathSearch = 3000;
    private int _pathBudget;

    private Route GetRoute(Citizen c)
    {
        int jobId = c.JobBuildingId ?? 0, homeId = c.HomeBuildingId ?? 0;
        bool cached = _routes.TryGetValue(c.Id, out var route);
        if (cached && route!.JobId == jobId && route.HomeId == homeId &&
            (route.Pathed && route.RoadVersion == _settlement.RoadVersion || _pathBudget <= 0))
            return route;

        bool usePaths = _pathBudget-- > 0;
        if (!usePaths && cached && route!.JobId == jobId && route.HomeId == homeId) return route;

        route = BuildRoute(c, jobId, homeId, usePaths);
        _routes[c.Id] = route;
        return route;
    }

    private readonly record struct Waypoint(Vector3 Point, (int X, int Y) WalkTile, float Dwell);

    private Route BuildRoute(Citizen c, int jobId, int homeId, bool usePaths)
    {
        ulong seed = (ulong)c.Id * 2654435761UL;
        float Rand(int salt) => (float)Hashing.Hash01(seed, salt, c.Id);
        Vector3 Near(Vector3 p, float spread, int salt) =>
            p + new Vector3((Rand(salt) - 0.5f) * spread, 0, (Rand(salt + 1) - 0.5f) * spread);
        (int, int) TileOf(Vector3 p) => Coords.WorldToTile(p);

        var home = homeId != 0 ? _settlement.FindBuilding(homeId) : null;
        var job = jobId != 0 ? _settlement.FindBuilding(jobId) : null;

        Waypoint homeStop = home != null
            ? new Waypoint(Coords.BuildingDoor(home), home.DoorTile, 2f)
            : At(new Vector3(_settlement.CenterX + Rand(1) * 6 - 3, 0, _settlement.CenterY + Rand(2) * 6 - 3), 2f);
        Waypoint At(Vector3 p, float dwell) => new(p, TileOf(p), dwell);

        Waypoint[] stops;
        if (job != null)
        {
            // Farmers and gardeners work out in the field; everyone else around the workplace door.
            bool field = job.IsOperational && (job.Def.Id == "farm" || job.Def.Id == "garden");
            Vector3 workPoint = field
                ? Coords.TileCorner(job.X, job.Y) + new Vector3(0.3f + Rand(3) * (job.Def.Width - 0.6f), 0, 0.3f + Rand(4) * (job.Def.Height - 0.6f))
                : Near(Coords.BuildingDoor(job), 0.8f, 5);
            var work = new Waypoint(workPoint, job.DoorTile, 7f);
            var stock = Logistics.NearestStockpile(_settlement, job);
            var stockStop = stock != null ? new Waypoint(Near(Coords.BuildingDoor(stock), 1.2f, 7), stock.DoorTile, 1.5f) : work;
            stops = new[] { homeStop, work, stockStop, work with { Point = Near(workPoint, 0.6f, 9) }, stockStop };
        }
        else
        {
            // Children and idle adults potter about near home.
            stops = new[] { homeStop with { Dwell = 3f }, At(Near(homeStop.Point, 5f, 11), 4f), At(Near(homeStop.Point, 5f, 13), 4f) };
        }

        // Expand the stops into a walkable polyline that follows roads and avoids buildings and water.
        var points = new List<Vector3>();
        var dwell = new List<float>();
        for (int i = 0; i < stops.Length; i++)
        {
            var from = stops[i];
            var to = stops[(i + 1) % stops.Length];
            points.Add(from.Point);
            dwell.Add(from.Dwell);
            if (!usePaths || from.WalkTile == to.WalkTile) continue;

            var path = Pathfinder.FindPath(_world.Map, from.WalkTile, to.WalkTile,
                Pathfinder.Mode.Walk, MaxPathSearch);
            if (path == null) continue;
            foreach (var (x, y) in path)
            {
                points.Add(Coords.TileCenter(x, y) + new Vector3((Rand(x + y) - 0.5f) * 0.3f, 0, (Rand(x - y) - 0.5f) * 0.3f));
                dwell.Add(0f);
            }
        }

        var route = new Route
        {
            Points = points.ToArray(),
            Dwell = dwell.ToArray(),
            Phase = Rand(17) * 60f,
            JobId = jobId,
            HomeId = homeId,
            Pathed = usePaths,
            RoadVersion = _settlement.RoadVersion,
        };
        for (int i = 0; i < route.Points.Length; i++)
            route.Length += route.Points[i].DistanceTo(route.Points[(i + 1) % route.Points.Length]) / WalkSpeed + route.Dwell[i];
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
