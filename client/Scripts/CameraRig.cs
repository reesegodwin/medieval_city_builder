using Godot;

namespace MedievalClient;

/// <summary>
/// Isometric-style strategy camera. WASD / arrows / screen edge / middle-drag to pan,
/// mouse wheel to zoom, Q/E to rotate in 90° steps.
/// </summary>
public partial class CameraRig : Node3D
{
    [Export] public float PanSpeed { get; set; } = 18f;
    [Export] public float MinDistance { get; set; } = 6f;
    [Export] public float MaxDistance { get; set; } = 90f;
    [Export] public float PitchDegrees { get; set; } = 52f;
    [Export] public int EdgePanMargin { get; set; } = 6;

    private Camera3D _camera = null!;
    private float _distance = 28f;
    private float _targetDistance = 28f;
    private float _yaw = Mathf.DegToRad(45f);
    private float _targetYaw = Mathf.DegToRad(45f);
    private bool _dragging;

    public Camera3D Camera => _camera;

    /// <summary>The ground point the camera looks at.</summary>
    public Vector3 Focus => Position;

    public override void _Ready()
    {
        _camera = new Camera3D { Fov = 40f, Far = 400f };
        AddChild(_camera);
        UpdateCamera();
    }

    public void LookAtTile(int x, int y) => Position = new Vector3(x + 0.5f, 0, y + 0.5f);

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _targetDistance = Mathf.Max(MinDistance, _targetDistance * 0.88f);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _targetDistance = Mathf.Min(MaxDistance, _targetDistance / 0.88f);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } mb:
                _dragging = mb.Pressed;
                break;
            case InputEventMouseMotion motion when _dragging:
                float factor = _distance * 0.0018f;
                Position -= Rotated(new Vector3(motion.Relative.X, 0, motion.Relative.Y) * factor);
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Q }:
                _targetYaw -= Mathf.Pi / 2;
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.E }:
                _targetYaw += Mathf.Pi / 2;
                break;
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var move = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) move.Z -= 1;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) move.Z += 1;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) move.X -= 1;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) move.X += 1;

        var viewport = GetViewport();
        var mouse = viewport.GetMousePosition();
        var size = viewport.GetVisibleRect().Size;
        bool mouseInside = mouse.X >= 0 && mouse.Y >= 0 && mouse.X <= size.X && mouse.Y <= size.Y &&
                           DisplayServer.WindowIsFocused();
        if (mouseInside)
        {
            if (mouse.X <= EdgePanMargin) move.X -= 1;
            if (mouse.X >= size.X - EdgePanMargin) move.X += 1;
            if (mouse.Y <= EdgePanMargin) move.Z -= 1;
            if (mouse.Y >= size.Y - EdgePanMargin) move.Z += 1;
        }

        if (move != Vector3.Zero)
            Position += Rotated(move.Normalized()) * PanSpeed * dt * (_distance / 28f);

        _distance = Mathf.Lerp(_distance, _targetDistance, 1 - Mathf.Exp(-10 * dt));
        _yaw = Mathf.LerpAngle(_yaw, _targetYaw, 1 - Mathf.Exp(-10 * dt));
        UpdateCamera();
    }

    /// <summary>Converts a screen-aligned pan direction to world space for the current yaw.</summary>
    private Vector3 Rotated(Vector3 v) => v.Rotated(Vector3.Up, _yaw);

    private void UpdateCamera()
    {
        if (_camera == null) return;
        float pitch = Mathf.DegToRad(PitchDegrees);
        var offset = new Vector3(0, Mathf.Sin(pitch), Mathf.Cos(pitch)) * _distance;
        offset = offset.Rotated(Vector3.Up, _yaw);
        _camera.Position = offset;
        _camera.LookAt(GlobalPosition, Vector3.Up);
    }

    /// <summary>Where the mouse ray hits the ground plane (y = 0), if anywhere.</summary>
    public Vector3? MouseGroundPoint()
    {
        var mouse = GetViewport().GetMousePosition();
        var origin = _camera.ProjectRayOrigin(mouse);
        var dir = _camera.ProjectRayNormal(mouse);
        if (Mathf.Abs(dir.Y) < 1e-4f) return null;
        float t = -origin.Y / dir.Y;
        return t > 0 ? origin + dir * t : null;
    }
}
