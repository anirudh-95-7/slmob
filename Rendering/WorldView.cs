using LibreMetaverse;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using SLMobileViewer.Services;

namespace SLMobileViewer.Rendering;

public sealed record PickResult(bool IsAvatar, uint LocalId, UUID Id, string Name);

/// <summary>
/// Software 3D renderer: third-person camera, per-primitive-type geometry, depth fog,
/// floating name labels, tap-to-select picking, and a top-down minimap.
/// </summary>
public sealed class WorldView : SKCanvasView
{
    public SpatialCullEngine? Cull { get; set; }
    public WorldService? World { get; set; }
    public SceneBaker? Baker { get; set; }

    /// <summary>True while the user is dragging: draw a reduced subset for responsiveness.</summary>
    public bool FastMode { get; private set; }

    public float Yaw { get; set; }
    public float Pitch { get; set; } = 0.35f;
    public float Distance { get; set; } = 8f;
    public int MaxPrims { get; set; } = 200;
    public bool LightTheme { get; set; }
    public uint SelectedLocalId { get; set; }

    public event Action<PickResult>? Picked;

    private const float FovTan = 0.637f;

    private readonly List<(SKPoint pt, float radius, float depth, bool isAvatar, uint localId, UUID id, string name)> _hits = new();
    private readonly Dictionary<long, SKPoint> _touches = new();
    private SKPoint _touchDown, _lastSingle;
    private DateTime _touchTime;
    private float _lastPinch;
    private bool _moved;

    public WorldView()
    {
        EnableTouchEvents = true;
        Touch += OnTouch;
    }

    private void OnTouch(object? sender, SKTouchEventArgs e)
    {
        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:
                _touches[e.Id] = e.Location;
                if (_touches.Count == 1)
                {
                    _touchDown = _lastSingle = e.Location;
                    _touchTime = DateTime.UtcNow;
                    _moved = false;
                }
                else if (_touches.Count == 2) _lastPinch = PinchDistance();
                break;

            case SKTouchAction.Moved:
                if (!_touches.ContainsKey(e.Id)) break;
                _touches[e.Id] = e.Location;

                FastMode = true;
                if (_touches.Count == 1)
                {
                    float dx = e.Location.X - _lastSingle.X;
                    float dy = e.Location.Y - _lastSingle.Y;
                    Yaw -= dx * 0.0035f;
                    Pitch = Math.Clamp(Pitch + dy * 0.0028f, -0.35f, 1.35f);
                    _lastSingle = e.Location;
                    if (MathF.Abs(e.Location.X - _touchDown.X) > 14 ||
                        MathF.Abs(e.Location.Y - _touchDown.Y) > 14) _moved = true;
                }
                else if (_touches.Count >= 2)
                {
                    _moved = true;
                    float d = PinchDistance();
                    if (_lastPinch > 1f && d > 1f)
                        Distance = Math.Clamp(Distance * (_lastPinch / d), 1.5f, 70f);
                    _lastPinch = d;
                }
                break;

            case SKTouchAction.Released:
            case SKTouchAction.Cancelled:
                if (_touches.Count == 1 && !_moved &&
                    (DateTime.UtcNow - _touchTime).TotalMilliseconds < 450)
                    DoPick(e.Location);
                _touches.Remove(e.Id);
                if (_touches.Count == 0) { FastMode = false; InvalidateSurface(); }
                if (_touches.Count < 2) _lastPinch = 0;
                break;
        }
        e.Handled = true;
    }

    private float PinchDistance()
    {
        if (_touches.Count < 2) return 0;
        var pts = _touches.Values.Take(2).ToArray();
        float dx = pts[0].X - pts[1].X, dy = pts[0].Y - pts[1].Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private void DoPick(SKPoint p)
    {
        (float d, PickResult? r) best = (float.MaxValue, null);
        lock (_hits)
        {
            foreach (var h in _hits)
            {
                float rad = MathF.Max(h.radius, 26f);
                float dx = p.X - h.pt.X, dy = p.Y - h.pt.Y;
                if (dx * dx + dy * dy > rad * rad) continue;
                if (h.depth < best.d)
                    best = (h.depth, new PickResult(h.isAvatar, h.localId, h.id, h.name));
            }
        }
        if (best.r != null) Picked?.Invoke(best.r);
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        int w = e.Info.Width, h = e.Info.Height;

        SKColor skyTop = LightTheme ? new SKColor(0xBF, 0xD8, 0xF0) : new SKColor(0x0E, 0x16, 0x22);
        SKColor skyBot = LightTheme ? new SKColor(0xE8, 0xF1, 0xFA) : new SKColor(0x1D, 0x2C, 0x3E);
        SKColor groundCol = LightTheme ? new SKColor(0xC9, 0xD6, 0xC7) : new SKColor(0x1A, 0x26, 0x20);
        SKColor fogCol = LightTheme ? new SKColor(0xDC, 0xE8, 0xF4) : new SKColor(0x18, 0x25, 0x34);

        using (var sky = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h),
                new[] { skyTop, skyBot }, null, SKShaderTileMode.Clamp)
        })
            canvas.DrawRect(0, 0, w, h, sky);

        if (Cull == null || World == null)
        {
            DrawCenterText(canvas, w, h, "Not connected");
            return;
        }

        var self = Cull.AvatarPosition();
        float far = Cull.CullRadius;
        var prims = Cull.SnapshotPrims();
        var avatars = World.SnapshotAvatars(self, far);

        // ---- camera ----
        var target = new Vector3(self.X, self.Y, self.Z + 1.2f);
        float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
        float cyaw = MathF.Cos(Yaw), syaw = MathF.Sin(Yaw);
        var eye = new Vector3(target.X - cp * cyaw * Distance,
                              target.Y - cp * syaw * Distance,
                              target.Z + sp * Distance);
        var fwd = Norm(new Vector3(cp * cyaw, cp * syaw, -sp));
        var right = Norm(Cross(fwd, new Vector3(0, 0, 1)));
        var up = Cross(right, fwd);
        float focal = (w * 0.5f) / FovTan;
        float ccx = w * 0.5f, ccy = h * 0.5f;

        bool Project(Vector3 p, out SKPoint s, out float depth)
        {
            var rel = new Vector3(p.X - eye.X, p.Y - eye.Y, p.Z - eye.Z);
            depth = Dot(rel, fwd);
            if (depth < 0.25f) { s = default; return false; }
            s = new SKPoint(ccx + focal * Dot(rel, right) / depth,
                            ccy - focal * Dot(rel, up) / depth);
            return true;
        }

        SKColor Fog(SKColor c, float depth)
        {
            float t = Math.Clamp((depth - 2f) / MathF.Max(far - 2f, 1f), 0f, 0.82f);
            return new SKColor(
                (byte)(c.Red + (fogCol.Red - c.Red) * t),
                (byte)(c.Green + (fogCol.Green - c.Green) * t),
                (byte)(c.Blue + (fogCol.Blue - c.Blue) * t));
        }

        DrawGround(canvas, self, Project, far, groundCol, fogCol);

        lock (_hits) _hits.Clear();
        var light = Norm(new Vector3(0.45f, 0.25f, 0.95f));

        // ---- baked static geometry (painter's algorithm) ----
        var tris = Baker?.Triangles ?? Array.Empty<BakedTri>();
        if (tris.Length > 0)
        {
            int limit = FastMode ? Math.Min(tris.Length, 3000) : tris.Length;

            var order = new (float depth, int idx)[limit];
            int visible = 0;
            for (int i = 0; i < limit; i++)
            {
                ref readonly var t = ref tris[i];
                var toEye = new Vector3(eye.X - t.Centroid.X, eye.Y - t.Centroid.Y, eye.Z - t.Centroid.Z);
                if (Dot(t.Normal, toEye) <= 0) continue;                 // backface
                float d = Dot(new Vector3(t.Centroid.X - eye.X, t.Centroid.Y - eye.Y, t.Centroid.Z - eye.Z), fwd);
                if (d < 0.25f || d > far + 12f) continue;                 // behind / beyond
                order[visible++] = (d, i);
            }
            Array.Sort(order, 0, visible, Comparer<(float depth, int idx)>.Create(
                (a, b) => b.depth.CompareTo(a.depth)));

            using var fill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
            using var path = new SKPath();

            for (int k = 0; k < visible; k++)
            {
                ref readonly var t = ref tris[order[k].idx];
                if (!Project(t.A, out var pa, out _) ||
                    !Project(t.B, out var pb, out _) ||
                    !Project(t.C, out var pc, out _)) continue;

                path.Rewind();
                path.MoveTo(pa); path.LineTo(pb); path.LineTo(pc); path.Close();

                float lam = Math.Clamp(Dot(t.Normal, light), 0f, 1f) * 0.62f + 0.42f;
                var c = Scale(t.Color, lam);
                if (t.LocalId == SelectedLocalId) c = Scale(c, 1.45f);
                fill.Color = Fog(c, order[k].depth);
                canvas.DrawPath(path, fill);
            }
        }

        // ---- pick targets from live prims (cheap, centre-based) ----
        int np = 0;
        foreach (var prim in prims)
        {
            if (np++ > 400) break;
            if (!Project(prim.Position, out var cpt, out var cdepth)) continue;
            float rr = MathF.Max(prim.Scale.X, MathF.Max(prim.Scale.Y, prim.Scale.Z)) * 0.5f;
            float rpx = focal * rr / cdepth;
            if (rpx < 2f) continue;
            lock (_hits) _hits.Add((cpt, rpx, cdepth, false, prim.LocalID, prim.ID, Cull.NameFor(prim)));
        }

        // ---- avatars (live, drawn as simple humanoids) ----
        var avDraw = new List<(float d, Action a)>();
        foreach (var av in avatars)
        {
            if (Project(av.Position, out var apt, out var adepth))
                lock (_hits) _hits.Add((apt, MathF.Max(200f / MathF.Max(adepth, 0.5f), 26f), adepth, true, 0, av.Id, av.Name));
            var a = av;
            float dist = Vector3.Distance(eye, av.Position);
            avDraw.Add((dist, () => DrawHumanoid(canvas, a.Position, a.Name, Project, Fog, light,
                new SKColor(0xD8, 0x6A, 0xC0))));
        }
        avDraw.Sort((x, y) => y.d.CompareTo(x.d));
        foreach (var d in avDraw) d.a();

        DrawHumanoid(canvas, new Vector3(self.X, self.Y, self.Z), "", Project, Fog, light,
            new SKColor(0x4F, 0xB0, 0xE8));
        DrawLabels(canvas, self, prims, Project, far);
        DrawMinimap(canvas, w, h, self, prims, avatars, far);
        DrawHud(canvas, w, h, self, prims.Count, avatars.Count);
    }

    // ---------------- geometry ----------------

    private static void DrawPrim(SKCanvas canvas, Primitive prim, ProjectFn project,
        Vector3 eye, Vector3 light, FogFn fog, bool selected)
    {
        var baseColor = ColorOf(prim);
        var s = prim.Scale; var rot = prim.Rotation; var pos = prim.Position;

        PrimType type;
        try { type = prim.Type; } catch { type = PrimType.Box; }

        if (type == PrimType.Sphere)
        {
            if (!project(pos, out var c, out var depth)) return;
            float radius = MathF.Max(s.X, MathF.Max(s.Y, s.Z)) * 0.5f;
            float rpx = 1000f * radius / depth * 0.9f;
            if (rpx < 1.5f) return;
            var lit = fog(Scale(baseColor, 1.15f), depth);
            var dark = fog(Scale(baseColor, 0.45f), depth);
            using var p = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(c.X - rpx * 0.35f, c.Y - rpx * 0.35f), rpx * 1.5f,
                    new[] { lit, dark }, null, SKShaderTileMode.Clamp)
            };
            canvas.DrawCircle(c, rpx, p);
            if (selected) StrokeCircle(canvas, c, rpx);
            return;
        }

        int sides = type switch
        {
            PrimType.Cylinder or PrimType.Torus or PrimType.Tube or PrimType.Ring => 10,
            PrimType.Prism => 3,
            _ => 0     // box-like
        };

        if (sides == 0)
            DrawBox(canvas, pos, rot, s, baseColor, project, eye, light, fog, selected);
        else
            DrawCylinder(canvas, pos, rot, s, sides, baseColor, project, eye, light, fog, selected);
    }

    private static void DrawBox(SKCanvas canvas, Vector3 pos, Quaternion rot, Vector3 s,
        SKColor col, ProjectFn project, Vector3 eye, Vector3 light, FogFn fog, bool selected)
    {
        float hx = s.X * .5f, hy = s.Y * .5f, hz = s.Z * .5f;
        var local = new[]
        {
            new Vector3(-hx,-hy,-hz), new Vector3(hx,-hy,-hz), new Vector3(hx,hy,-hz), new Vector3(-hx,hy,-hz),
            new Vector3(-hx,-hy, hz), new Vector3(hx,-hy, hz), new Vector3(hx,hy, hz), new Vector3(-hx,hy, hz)
        };
        var world = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var r = Rotate(local[i], rot);
            world[i] = new Vector3(pos.X + r.X, pos.Y + r.Y, pos.Z + r.Z);
        }

        int[][] faces = { new[]{0,1,2,3}, new[]{4,5,6,7}, new[]{0,1,5,4}, new[]{1,2,6,5}, new[]{2,3,7,6}, new[]{3,0,4,7} };
        Vector3[] norms = { new(0,0,-1), new(0,0,1), new(0,-1,0), new(1,0,0), new(0,1,0), new(-1,0,0) };

        for (int f = 0; f < 6; f++)
            DrawFace(canvas, world, faces[f], Rotate(norms[f], rot), col, project, eye, light, fog, selected);
    }

    private static void DrawCylinder(SKCanvas canvas, Vector3 pos, Quaternion rot, Vector3 s, int sides,
        SKColor col, ProjectFn project, Vector3 eye, Vector3 light, FogFn fog, bool selected)
    {
        float rx = s.X * .5f, ry = s.Y * .5f, hz = s.Z * .5f;
        var bot = new Vector3[sides];
        var top = new Vector3[sides];
        var radial = new Vector3[sides];

        for (int i = 0; i < sides; i++)
        {
            float a = (float)(2 * Math.PI * i / sides);
            float cxl = MathF.Cos(a) * rx, cyl = MathF.Sin(a) * ry;
            var rb = Rotate(new Vector3(cxl, cyl, -hz), rot);
            var rt = Rotate(new Vector3(cxl, cyl, hz), rot);
            bot[i] = new Vector3(pos.X + rb.X, pos.Y + rb.Y, pos.Z + rb.Z);
            top[i] = new Vector3(pos.X + rt.X, pos.Y + rt.Y, pos.Z + rt.Z);
            radial[i] = Rotate(new Vector3(MathF.Cos(a), MathF.Sin(a), 0), rot);
        }

        // sides
        var quad = new Vector3[4];
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            quad[0] = bot[i]; quad[1] = bot[j]; quad[2] = top[j]; quad[3] = top[i];
            DrawFace(canvas, quad, new[] { 0, 1, 2, 3 },
                Norm(new Vector3((radial[i].X + radial[j].X) * .5f,
                                 (radial[i].Y + radial[j].Y) * .5f,
                                 (radial[i].Z + radial[j].Z) * .5f)),
                col, project, eye, light, fog, selected);
        }

        // caps
        var capIdx = new int[sides];
        for (int i = 0; i < sides; i++) capIdx[i] = i;
        DrawFace(canvas, top, capIdx, Rotate(new Vector3(0, 0, 1), rot), col, project, eye, light, fog, selected);
        DrawFace(canvas, bot, capIdx, Rotate(new Vector3(0, 0, -1), rot), col, project, eye, light, fog, selected);
    }

    private static void DrawFace(SKCanvas canvas, Vector3[] verts, int[] idx, Vector3 normal,
        SKColor col, ProjectFn project, Vector3 eye, Vector3 light, FogFn fog, bool selected)
    {
        var fc = Center(verts, idx);
        var toEye = new Vector3(eye.X - fc.X, eye.Y - fc.Y, eye.Z - fc.Z);
        if (Dot(normal, toEye) <= 0) return;

        using var path = new SKPath();
        float depthSum = 0;
        for (int i = 0; i < idx.Length; i++)
        {
            if (!project(verts[idx[i]], out var sp, out var dp)) return;
            depthSum += dp;
            if (i == 0) path.MoveTo(sp); else path.LineTo(sp);
        }
        path.Close();
        float depth = depthSum / idx.Length;

        float lam = Math.Clamp(Dot(Norm(normal), light), 0f, 1f) * 0.65f + 0.4f;
        using var fill = new SKPaint { Color = fog(Scale(col, lam), depth), IsAntialias = true };
        canvas.DrawPath(path, fill);

        using var edge = new SKPaint
        {
            Color = selected ? new SKColor(0xFF, 0xD5, 0x4F) : fog(Scale(col, 0.35f), depth).WithAlpha(120),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = selected ? 3f : 1f
        };
        canvas.DrawPath(path, edge);
    }

    private static void StrokeCircle(SKCanvas canvas, SKPoint c, float r)
    {
        using var p = new SKPaint
        {
            Color = new SKColor(0xFF, 0xD5, 0x4F), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 3
        };
        canvas.DrawCircle(c, r, p);
    }

    private static void DrawAvatar(SKCanvas canvas, NearbyAvatar av, ProjectFn project, FogFn fog)
    {
        var feet = new Vector3(av.Position.X, av.Position.Y, av.Position.Z - 0.95f);
        var head = new Vector3(av.Position.X, av.Position.Y, av.Position.Z + 0.95f);
        if (!project(feet, out var pf, out var d1) || !project(head, out var ph, out _)) return;

        float width = MathF.Max(3f, 240f / MathF.Max(d1, 0.5f));
        using var body = new SKPaint
        {
            Color = fog(new SKColor(0xE0, 0x60, 0xC0), d1), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawLine(pf, ph, body);
        using var hd = new SKPaint { Color = fog(new SKColor(0xFF, 0xC8, 0xE8), d1), IsAntialias = true };
        canvas.DrawCircle(ph.X, ph.Y - width * .5f, width * .55f, hd);

        DrawTag(canvas, av.Name, ph.X, ph.Y - width * 1.3f - 8, 26, new SKColor(0xFF, 0xD5, 0xF0));
    }

    private static void DrawSelf(SKCanvas canvas, Vector3 self, ProjectFn project)
    {
        var feet = new Vector3(self.X, self.Y, self.Z - 0.95f);
        var head = new Vector3(self.X, self.Y, self.Z + 0.95f);
        if (!project(feet, out var pf, out var d) || !project(head, out var ph, out _)) return;
        float width = MathF.Max(4f, 240f / MathF.Max(d, 0.5f));
        using var body = new SKPaint
        {
            Color = new SKColor(0x4F, 0xC3, 0xF7), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawLine(pf, ph, body);
        using var hp = new SKPaint { Color = new SKColor(0xB3, 0xE5, 0xFC), IsAntialias = true };
        canvas.DrawCircle(ph.X, ph.Y - width * .5f, width * .55f, hp);
    }

    /// <summary>Simple articulated figure: head, torso, arms, legs.</summary>
    private static void DrawHumanoid(SKCanvas canvas, Vector3 pos, string name,
        ProjectFn project, FogFn fog, Vector3 light, SKColor skin)
    {
        var torso = Scale(skin, 0.85f);
        var limb = Scale(skin, 0.7f);

        // centre, size  (SL: Z up; avatar ~1.9 m tall centred on pos)
        DrawSolidBox(canvas, Off(pos, 0, 0, 0.72f), new Vector3(0.24f, 0.24f, 0.26f), skin, project, fog, light);   // head
        DrawSolidBox(canvas, Off(pos, 0, 0, 0.25f), new Vector3(0.38f, 0.22f, 0.62f), torso, project, fog, light);  // torso
        DrawSolidBox(canvas, Off(pos, 0, 0.26f, 0.26f), new Vector3(0.13f, 0.13f, 0.58f), limb, project, fog, light);
        DrawSolidBox(canvas, Off(pos, 0, -0.26f, 0.26f), new Vector3(0.13f, 0.13f, 0.58f), limb, project, fog, light);
        DrawSolidBox(canvas, Off(pos, 0, 0.11f, -0.45f), new Vector3(0.15f, 0.15f, 0.78f), limb, project, fog, light);
        DrawSolidBox(canvas, Off(pos, 0, -0.11f, -0.45f), new Vector3(0.15f, 0.15f, 0.78f), limb, project, fog, light);

        if (!string.IsNullOrEmpty(name) &&
            project(Off(pos, 0, 0, 1.05f), out var tag, out _))
            DrawTag(canvas, name, tag.X, tag.Y, 24, new SKColor(0xFF, 0xD5, 0xF0));
    }

    private static Vector3 Off(Vector3 p, float dx, float dy, float dz)
        => new(p.X + dx, p.Y + dy, p.Z + dz);

    private static void DrawSolidBox(SKCanvas canvas, Vector3 c, Vector3 size, SKColor col,
        ProjectFn project, FogFn fog, Vector3 light)
    {
        float hx = size.X * .5f, hy = size.Y * .5f, hz = size.Z * .5f;
        var w = new[]
        {
            new Vector3(c.X-hx, c.Y-hy, c.Z-hz), new Vector3(c.X+hx, c.Y-hy, c.Z-hz),
            new Vector3(c.X+hx, c.Y+hy, c.Z-hz), new Vector3(c.X-hx, c.Y+hy, c.Z-hz),
            new Vector3(c.X-hx, c.Y-hy, c.Z+hz), new Vector3(c.X+hx, c.Y-hy, c.Z+hz),
            new Vector3(c.X+hx, c.Y+hy, c.Z+hz), new Vector3(c.X-hx, c.Y+hy, c.Z+hz)
        };
        int[][] faces = { new[]{0,1,2,3}, new[]{4,5,6,7}, new[]{0,1,5,4}, new[]{1,2,6,5}, new[]{2,3,7,6}, new[]{3,0,4,7} };
        Vector3[] norms = { new(0,0,-1), new(0,0,1), new(0,-1,0), new(1,0,0), new(0,1,0), new(-1,0,0) };

        var order = new List<(float d, int f)>(6);
        var pts = new SKPoint[8];
        var depths = new float[8];
        for (int i = 0; i < 8; i++)
            if (!project(w[i], out pts[i], out depths[i])) return;

        for (int f = 0; f < 6; f++)
        {
            float d = 0;
            foreach (var i in faces[f]) d += depths[i];
            order.Add((d / 4f, f));
        }
        order.Sort((a, b) => b.d.CompareTo(a.d));

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var path = new SKPath();
        foreach (var (d, f) in order)
        {
            path.Rewind();
            path.MoveTo(pts[faces[f][0]]);
            for (int i = 1; i < 4; i++) path.LineTo(pts[faces[f][i]]);
            path.Close();
            float lam = Math.Clamp(Dot(norms[f], light), 0f, 1f) * 0.6f + 0.45f;
            paint.Color = fog(Scale(col, lam), d);
            canvas.DrawPath(path, paint);
        }
    }

    private void DrawLabels(SKCanvas canvas, Vector3 self, List<Primitive> prims, ProjectFn project, float far)
    {
        if (Cull == null) return;
        var near = prims
            .Select(p => (p, d: Vector3.Distance(self, p.Position)))
            .Where(t => t.d < MathF.Min(far, 18f))
            .OrderBy(t => t.d).Take(10);

        foreach (var (p, _) in near)
        {
            var topZ = p.Position.Z + p.Scale.Z * 0.5f + 0.25f;
            if (!project(new Vector3(p.Position.X, p.Position.Y, topZ), out var sp, out _)) continue;
            var name = Cull.NameFor(p);
            if (name.StartsWith("Object ")) continue;      // unresolved, skip clutter
            DrawTag(canvas, name, sp.X, sp.Y, 22,
                p.LocalID == SelectedLocalId ? new SKColor(0xFF, 0xD5, 0x4F) : SKColors.White);
        }
    }

    private static void DrawTag(SKCanvas canvas, string text, float x, float y, float size, SKColor color)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > 26) text = text[..25] + "…";
        using var outline = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 200), IsAntialias = true, TextSize = size,
            TextAlign = SKTextAlign.Center, Style = SKPaintStyle.Stroke, StrokeWidth = 4
        };
        using var fill = new SKPaint
        {
            Color = color, IsAntialias = true, TextSize = size, TextAlign = SKTextAlign.Center
        };
        canvas.DrawText(text, x, y, outline);
        canvas.DrawText(text, x, y, fill);
    }

    private static void DrawGround(SKCanvas canvas, Vector3 self, ProjectFn project,
        float radius, SKColor groundCol, SKColor fogCol)
    {
        float z = self.Z - 1.0f;
        float r = MathF.Min(radius, 80f);

        // filled ground quad
        var corners = new[]
        {
            new Vector3(self.X - r, self.Y - r, z), new Vector3(self.X + r, self.Y - r, z),
            new Vector3(self.X + r, self.Y + r, z), new Vector3(self.X - r, self.Y + r, z)
        };
        using (var path = new SKPath())
        {
            bool ok = true;
            for (int i = 0; i < 4; i++)
            {
                if (!project(corners[i], out var sp, out _)) { ok = false; break; }
                if (i == 0) path.MoveTo(sp); else path.LineTo(sp);
            }
            if (ok)
            {
                path.Close();
                using var g = new SKPaint { Color = groundCol, IsAntialias = true };
                canvas.DrawPath(path, g);
            }
        }

        using var grid = new SKPaint
        {
            Color = fogCol.WithAlpha(160), IsAntialias = true,
            StrokeWidth = 1, Style = SKPaintStyle.Stroke
        };
        float step = 4f;
        for (float x = MathF.Floor((self.X - r) / step) * step; x <= self.X + r; x += step)
            if (project(new Vector3(x, self.Y - r, z), out var a, out _) &&
                project(new Vector3(x, self.Y + r, z), out var b, out _))
                canvas.DrawLine(a, b, grid);
        for (float y = MathF.Floor((self.Y - r) / step) * step; y <= self.Y + r; y += step)
            if (project(new Vector3(self.X - r, y, z), out var a, out _) &&
                project(new Vector3(self.X + r, y, z), out var b, out _))
                canvas.DrawLine(a, b, grid);
    }

    private void DrawMinimap(SKCanvas canvas, int w, int h, Vector3 self,
        List<Primitive> prims, IReadOnlyList<NearbyAvatar> avatars, float radius)
    {
        float size = Math.Clamp(MathF.Min(w, h) * 0.28f, 110f, 230f);
        float pad = 12f;
        float cx = w - size / 2 - pad, cy = size / 2 + pad;
        float rpx = size / 2 - 4;
        float sc = rpx / MathF.Max(radius, 1f);

        using (var bg = new SKPaint { Color = new SKColor(0, 0, 0, 140), IsAntialias = true })
            canvas.DrawCircle(cx, cy, rpx + 3, bg);
        using (var ring = new SKPaint
        {
            Color = new SKColor(0x7F, 0xB8, 0xE8, 170), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 2
        })
        {
            canvas.DrawCircle(cx, cy, rpx + 3, ring);
            canvas.DrawCircle(cx, cy, rpx * .5f, ring);
        }

        using var pp = new SKPaint { Color = new SKColor(0xA8, 0xBC, 0xCC, 225) };
        foreach (var p in prims)
        {
            float dx = (p.Position.X - self.X) * sc, dy = (p.Position.Y - self.Y) * sc;
            if (dx * dx + dy * dy > rpx * rpx) continue;
            if (p.LocalID == SelectedLocalId)
            {
                using var selP = new SKPaint { Color = new SKColor(0xFF, 0xD5, 0x4F), IsAntialias = true };
                canvas.DrawCircle(cx + dx, cy - dy, 4f, selP);
            }
            else canvas.DrawRect(cx + dx - 1.2f, cy - dy - 1.2f, 2.4f, 2.4f, pp);
        }

        using var ap = new SKPaint { Color = new SKColor(0xE0, 0x60, 0xC0), IsAntialias = true };
        foreach (var a in avatars)
        {
            float dx = (a.Position.X - self.X) * sc, dy = (a.Position.Y - self.Y) * sc;
            if (dx * dx + dy * dy > rpx * rpx) continue;
            canvas.DrawCircle(cx + dx, cy - dy, 3.5f, ap);
        }

        using var me = new SKPaint { Color = new SKColor(0x4F, 0xC3, 0xF7), IsAntialias = true };
        canvas.DrawCircle(cx, cy, 4.5f, me);
        DrawTag(canvas, "N", cx, cy - rpx + 16, 20, SKColors.White);
    }

    private static void DrawHud(SKCanvas canvas, int w, int h, Vector3 self, int prims, int people)
    {
        using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 120) };
        canvas.DrawRoundRect(new SKRect(10, h - 46, 10 + 430, h - 8), 10, 10, bg);
        using var t = new SKPaint { Color = new SKColor(0xE8, 0xEE, 0xF4), IsAntialias = true, TextSize = 23 };
        canvas.DrawText($"<{self.X:0}, {self.Y:0}, {self.Z:0}>   objects {prims}   people {people}", 22, h - 20, t);
    }

    private static void DrawCenterText(SKCanvas canvas, int w, int h, string text)
    {
        using var t = new SKPaint
        { Color = SKColors.Gray, IsAntialias = true, TextSize = 30, TextAlign = SKTextAlign.Center };
        canvas.DrawText(text, w / 2f, h / 2f, t);
    }

    // ---------------- helpers ----------------

    private delegate bool ProjectFn(Vector3 p, out SKPoint s, out float depth);
    private delegate SKColor FogFn(SKColor c, float depth);

    private static Vector3 Rotate(Vector3 v, Quaternion q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float ix = w * v.X + y * v.Z - z * v.Y;
        float iy = w * v.Y + z * v.X - x * v.Z;
        float iz = w * v.Z + x * v.Y - y * v.X;
        float iw = -x * v.X - y * v.Y - z * v.Z;
        return new Vector3(
            ix * w + iw * -x + iy * -z - iz * -y,
            iy * w + iw * -y + iz * -x - ix * -z,
            iz * w + iw * -z + ix * -y - iy * -x);
    }

    private static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vector3 Cross(Vector3 a, Vector3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Vector3 Norm(Vector3 v)
    {
        float m = MathF.Sqrt(Dot(v, v));
        return m < 1e-6f ? new Vector3(0, 0, 1) : new Vector3(v.X / m, v.Y / m, v.Z / m);
    }

    private static Vector3 Center(Vector3[] verts, int[] idx)
    {
        float x = 0, y = 0, z = 0;
        foreach (var i in idx) { x += verts[i].X; y += verts[i].Y; z += verts[i].Z; }
        return new Vector3(x / idx.Length, y / idx.Length, z / idx.Length);
    }

    private static SKColor ColorOf(Primitive prim)
    {
        try
        {
            var rgba = prim.Textures?.DefaultTexture?.RGBA;
            if (rgba != null)
            {
                var c = rgba.Value;
                var col = new SKColor(
                    (byte)Math.Clamp(c.R * 255f, 0, 255),
                    (byte)Math.Clamp(c.G * 255f, 0, 255),
                    (byte)Math.Clamp(c.B * 255f, 0, 255));
                // pure white is the default "untextured" colour: tint it so shapes differ
                if (col.Red > 240 && col.Green > 240 && col.Blue > 240) return HashColor(prim.LocalID);
                return col;
            }
        }
        catch { }
        return HashColor(prim.LocalID);
    }

    private static SKColor HashColor(uint id) => new(
        (byte)(110 + (id * 37) % 120),
        (byte)(110 + (id * 61) % 120),
        (byte)(110 + (id * 97) % 120));

    private static SKColor Scale(SKColor c, float f) => new(
        (byte)Math.Clamp(c.Red * f, 0, 255),
        (byte)Math.Clamp(c.Green * f, 0, 255),
        (byte)Math.Clamp(c.Blue * f, 0, 255));
}
