using Godot;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;
using Sim.Core.War;
using MedievalClient.Ui;

namespace MedievalClient;

/// <summary>
/// Draws bandit warbands marching on a town as a loose crowd of dark figures (up to one per raider), with a red
/// banner. Only bands the player's watch can see are drawn (fog of war). The simulation moves a band once a day;
/// this slides it along its march in between, purely for looks.
/// </summary>
public partial class WarbandRenderer : Node3D
{
    private const int MaxFigures = 1024;
    private const int MaxDrawnPerBand = 60;
    private static readonly Color RaiderCloth = new(0.32f, 0.12f, 0.1f);
    private static readonly Color ArcherCloth = new(0.22f, 0.2f, 0.12f);
    private static readonly Color Skin = new(0.8f, 0.62f, 0.48f);
    private static readonly Color Banner = new(0.75f, 0.1f, 0.08f);

    private SimSession _session = null!;
    private MultiMesh _bodies = null!, _heads = null!, _banners = null!, _tents = null!;
    private readonly Dictionary<string, MultiMesh> _engines = new();
    private static readonly Color TentCloth = new(0.62f, 0.55f, 0.45f);

    public void Initialize(SimSession session)
    {
        _session = session;
        _bodies = TerrainRenderer.NewMultiMesh(LowPolyMeshes.CitizenBody, MaxFigures, colors: true);
        _heads = TerrainRenderer.NewMultiMesh(LowPolyMeshes.CitizenHead, MaxFigures, colors: true);
        _banners = TerrainRenderer.NewMultiMesh(LowPolyMeshes.WarBanner, 64, colors: true);
        _tents = TerrainRenderer.NewMultiMesh(LowPolyMeshes.Tent, 128, colors: true);
        _engines["ram"] = TerrainRenderer.NewMultiMesh(LowPolyMeshes.SiegeRam, 32);
        _engines["catapult"] = TerrainRenderer.NewMultiMesh(LowPolyMeshes.Catapult, 32);
        _engines["trebuchet"] = TerrainRenderer.NewMultiMesh(LowPolyMeshes.Trebuchet, 32);
        foreach (var mm in new[] { _bodies, _heads, _banners, _tents }.Concat(_engines.Values))
        {
            mm.VisibleInstanceCount = 0;
            AddChild(new MultiMeshInstance3D { Multimesh = mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
    }

    public override void _Process(double delta)
    {
        if (_session == null) return;
        var world = _session.World;
        var player = _session.PlayerTown;
        float dayFraction = (float)(world.Time.Tick % TimeScale.TicksPerDay) / TimeScale.TicksPerDay;
        float clock = Time.GetTicksMsec() / 1000f;
        int n = 0, flags = 0, tents = 0;
        var engineCounts = _engines.Keys.ToDictionary(k => k, _ => 0);

        foreach (var band in world.War.Warbands)
        {
            if (!ArmyPanel.InSight(player, band) || flags >= _banners.InstanceCount) continue;
            var target = world.FindSettlement(band.TargetId);
            if (target == null) continue;

            // Where the band is now: its daily position, slid toward the town by the share of today gone.
            var centre = new Vector2(target.CenterX + 0.5f, target.CenterY + 0.5f);
            var at = new Vector2((float)band.X, (float)band.Y);
            var toTown = centre - at;
            float left = Mathf.Max(0, toTown.Length() - (float)Raids.ArriveRadius(target));
            var dir = toTown.LengthSquared() > 0.01f ? toTown.Normalized() : Vector2.Up;
            at += dir * Mathf.Min(left, (float)world.Data.War.Raids.SpeedTilesPerDay * dayFraction);
            // A besieging band closes in on the wall it batters: draw its camp a stone's throw outside it.
            if (band.Siege && band.HasTarget)
            {
                var wall = new Vector2(band.TargetX + 0.5f, band.TargetY + 0.5f);
                var away = (at - wall).LengthSquared() > 0.01f ? (at - wall).Normalized() : -dir;
                at = wall + away * 11;
                dir = -away;
            }
            float yaw = Mathf.Atan2(dir.X, dir.Y);
            var side = new Vector2(dir.Y, -dir.X);

            int drawn = Math.Min(band.Size, MaxDrawnPerBand);
            int perRow = Math.Max(3, (int)Mathf.Sqrt(drawn) + 1);
            for (int i = 0; i < drawn && n < MaxFigures; i++, n++)
            {
                // A loose column: rows across the line of march, jittered so it reads as a mob, not a parade.
                int row = i / perRow, col = i % perRow;
                float jx = Jitter(band.Id, i, 1) * 0.5f, jy = Jitter(band.Id, i, 2) * 0.5f;
                var p = at + side * ((col - perRow / 2f) * 0.8f + jx) - dir * (row * 0.9f + jy);
                float y = Mathf.Max(TerrainShape.HeightAt(world.Map, p.X, p.Y), TerrainShape.WaterLevel) +
                          Mathf.Abs(Mathf.Sin(clock * 8f + i)) * 0.04f;
                var xform = new Transform3D(new Basis(Vector3.Up, yaw).Scaled(Vector3.One * CitizenRenderer.FigureScale), new Vector3(p.X, y, p.Y));
                bool archer = i < band.Archers * drawn / Math.Max(1, band.Size);
                _bodies.SetInstanceTransform(n, xform);
                _bodies.SetInstanceColor(n, archer ? ArcherCloth : RaiderCloth);
                _heads.SetInstanceTransform(n, xform);
                _heads.SetInstanceColor(n, Skin);
            }

            float by = Mathf.Max(TerrainShape.HeightAt(world.Map, at.X, at.Y), TerrainShape.WaterLevel);
            _banners.SetInstanceTransform(flags, new Transform3D(new Basis(Vector3.Up, yaw), new Vector3(at.X, by, at.Y)));
            _banners.SetInstanceColor(flags, Banner);
            flags++;
            if (band.Siege) DrawCamp(world, band, at, dir, side, ref tents, engineCounts);
        }
        _bodies.VisibleInstanceCount = n;
        _heads.VisibleInstanceCount = n;
        _banners.VisibleInstanceCount = flags;
        _tents.VisibleInstanceCount = tents;
        foreach (var (kind, mm) in _engines) mm.VisibleInstanceCount = engineCounts[kind];
    }

    /// <summary>A besieging band's camp: tents behind its lines, and its engines drawn up before the wall it batters.</summary>
    private void DrawCamp(Sim.Core.Simulation.GameWorld world, Warband band, Vector2 at, Vector2 dir, Vector2 side, ref int tents,
        Dictionary<string, int> engineCounts)
    {
        float Ground(Vector2 p) => Mathf.Max(TerrainShape.HeightAt(world.Map, p.X, p.Y), TerrainShape.WaterLevel);
        int tentCount = Math.Min(8, 2 + band.Size / 8);
        for (int i = 0; i < tentCount && tents < _tents.InstanceCount; i++, tents++)
        {
            var p = at - dir * (6 + (i / 4) * 2.2f) + side * ((i % 4 - 1.5f) * 2.2f + Jitter(band.Id, i, 7));
            _tents.SetInstanceTransform(tents, new Transform3D(new Basis(Vector3.Up, Mathf.Atan2(dir.X, dir.Y) + (i % 2) * 0.3f).Scaled(Vector3.One * 1.6f),
                new Vector3(p.X, Ground(p), p.Y)));
            _tents.SetInstanceColor(tents, TentCloth);
        }

        // Engines stand a few tiles out from the wall being battered (or in front of the band until it picks one).
        var target = band.HasTarget ? new Vector2(band.TargetX + 0.5f, band.TargetY + 0.5f) : at + dir * 6;
        var toWall = (target - at).LengthSquared() > 0.01f ? (target - at).Normalized() : dir;
        var across = new Vector2(toWall.Y, -toWall.X);
        float yaw = Mathf.Atan2(toWall.X, toWall.Y);
        for (int i = 0; i < band.Engines.Count; i++)
        {
            var engine = band.Engines[i];
            if (!_engines.TryGetValue(engine.Kind, out var mm) || engineCounts[engine.Kind] >= mm.InstanceCount) continue;
            float distance = engine.Kind == "ram" ? 1.6f : engine.Kind == "catapult" ? 5f : 7f;
            var p = target - toWall * distance + across * ((i - (band.Engines.Count - 1) / 2f) * 2.2f);
            // Half-built engines are drawn short.
            float build = engine.Built ? 1f : 0.35f + 0.65f * (float)engine.Progress;
            var basis = new Basis(Vector3.Up, yaw).Scaled(new Vector3(1.4f, 1.4f * build, 1.4f));
            mm.SetInstanceTransform(engineCounts[engine.Kind]++, new Transform3D(basis, new Vector3(p.X, Ground(p), p.Y)));
        }
    }

    private static float Jitter(int band, int i, int salt) =>
        (float)Sim.Core.Util.Hashing.Hash01((ulong)band * 2654435761UL, salt, i) - 0.5f;
}
