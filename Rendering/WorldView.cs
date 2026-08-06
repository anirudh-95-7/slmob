using LibreMetaverse;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using SLMobileViewer.Services;

namespace SLMobileViewer.Rendering;

/// <summary>
/// Software 3D renderer: third-person camera orbiting the avatar, primitives drawn
/// as shaded solids at their true position/rotation/scale, plus a top-down minimap.
/// No textures or mesh assets (those need JPEG2000 + mesh pipelines).
/// </summary>
public sealed class WorldView : SKCanvasView
{
    public SpatialCullEngine? Cull { get; set; }
    public WorldService? World { get; set; }

    // Camera orbit state (driven by touch gestures)
    public float Yaw { get; set; } = 0f;          // radians
    public float Pitch { get; set; } = 0.35f;     // radians, above horizon
    public float Distance { get; set; } = 7f;     // metres behind avatar
    public int MaxPrims { get; set; } = 220;

    private const float FovTan = 0.637f;          // tan(65deg / 2)

    private static readonly SKColor Sky = new(0x14, 0x1E, 0x2B);
    private static readonly SKColor Ground = new(0x1E, 0x2C, 0x24);

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        int w = e.Info.Width, h = e.Info.Height;
        canvas.Clear(Sky);

        if (Cull == null || World == null)
        {
            DrawCenterText(canvas, w, h, "Not connected");
            return;
        }

        var self = Cull.AvatarPosition();
        var prims = Cull.SnapshotPrims();
        var avatars = World.SnapshotAvatars(self, Cull.CullRadius);

        // ---- camera basis ----
        var target = new Vector3(self.X, self.Y, self.Z + 1.2f);
        float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
        float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);

        var eye = new Vector3(
            target.X - cp * cy * Distance,
            target.Y - cp * sy * Distance,
            target.Z + sp * Distance);

        var fwd = Norm(new Vector3(cp * cy, cp * sy, -sp));
        var right = Norm(Cross(fwd, new Vector3(0, 0, 1)));
        var up = Cross(right, fwd);

        float focal = (w * 0.5f) / FovTan;
        float cx = w * 0.5f, cyc = h * 0.5f;

        bool Project(Vector3 p, out SKPoint sPt, out float depth)
        {
            var rel = new Vector3(p.X - eye.X, p.Y - eye.Y, p.Z - eye.Z);
            depth = Dot(rel, fwd);
            if (depth < 0.2f) { sPt = default; return false; }
            sPt = new SKPoint(cx + focal * Dot(rel, right) / depth,
                              cyc - focal * Dot(rel, up) / depth);
            return true;
        }

        // ---- horizon / ground wash ----
        using (var gp = new SKPaint { Color = Ground, IsAntialias = false })
        {
            float horizonY = cyc + focal * sp / MathF.Max(cp, 0.001f) * 0.0f;
            canvas.DrawRect(0, Math.Clamp(horizonY, 0, h), w, h, gp);
        }

        DrawGroundGrid(canvas, self, Project, Cull.CullRadius);

        // ---- collect drawables, painter's algorithm (far to near) ----
        var items = new List<(float dist, Action draw)>();
        var lightDir = Norm(new Vector3(0.4f, 0.3f, 1.0f));

        int count = 0;
        foreach (var prim in prims)
        {
            if (count++ > MaxPrims) break;
            float d = Vector3.Distance(eye, prim.Position);
            var pr = prim;
            items.Add((d, () => DrawPrim(canvas, pr, Project, eye, lightDir)));
        }

        foreach (var av in avatars)
        {
            float d = Vector3.Distance(eye, av.Position);
            var a = av;
            items.Add((d, () => DrawAvatar(canvas, a, Project)));
        }

        items.Sort((x, y) => y.dist.CompareTo(x.dist));
        foreach (var it in items) it.draw();

        DrawSelf(canvas, self, Project);
        DrawMinimap(canvas, w, h, self, prims, avatars, Cull.CullRadius);
        DrawHud(canvas, w, h, self, prims.Count, avatars.Count);
    }

    // ---------------- primitives ----------------

    private static void DrawPrim(SKCanvas canvas, Primitive prim,
        ProjectFn project, Vector3 eye, Vector3 light)
    {
        var baseColor = ColorOf(prim);
        var scale = prim.Scale;
        var rot = prim.Rotation;
        var pos = prim.Position;

        PrimType type;
        try { type = prim.Type; } catch { type = PrimType.Box; }

        if (type == PrimType.Sphere)
        {
            if (!project(pos, out var c, out var depth)) return;
            float radius = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)) * 0.5f;
            float rpx = (float)(radius / depth) * 900f;
            if (rpx < 1.2f) return;
            using var p = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(c.X - rpx * 0.3f, c.Y - rpx * 0.3f), rpx * 1.4f,
                    new[] { Lighten(baseColor, 1.25f), Darken(baseColor, 0.55f) },
                    null, SKShaderTileMode.Clamp)
            };
            canvas.DrawCircle(c, rpx, p);
            return;
        }

        // Everything else approximated as an oriented box
        float hx = scale.X * 0.5f, hy = scale.Y * 0.5f, hz = scale.Z * 0.5f;
        var local = new Vector3[8];
        local[0] = new Vector3(-hx, -hy, -hz); local[1] = new Vector3(hx, -hy, -hz);
        local[2] = new Vector3(hx, hy, -hz);   local[3] = new Vector3(-hx, hy, -hz);
        local[4] = new Vector3(-hx, -hy, hz);  local[5] = new Vector3(hx, -hy, hz);
        local[6] = new Vector3(hx, hy, hz);    local[7] = new Vector3(-hx, hy, hz);

        var world = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var r = Rotate(local[i], rot);
            world[i] = new Vector3(pos.X + r.X, pos.Y + r.Y, pos.Z + r.Z);
        }

        int[][] faces =
        {
            new[]{0,1,2,3}, new[]{4,5,6,7}, new[]{0,1,5,4},
            new[]{1,2,6,5}, new[]{2,3,7,6}, new[]{3,0,4,7}
        };
        Vector3[] normals =
        {
            new(0,0,-1), new(0,0,1), new(0,-1,0),
            new(1,0,0),  new(0,1,0), new(-1,0,0)
        };

        for (int f = 0; f < 6; f++)
        {
            var n = Rotate(normals[f], rot);
            var fc = Center(world, faces[f]);
            var toEye = new Vector3(eye.X - fc.X, eye.Y - fc.Y, eye.Z - fc.Z);
            if (Dot(n, toEye) <= 0) continue;                 // backface

            var path = new SKPath();
            bool ok = true;
            for (int i = 0; i < 4; i++)
            {
                if (!project(world[faces[f][i]], out var sp, out _)) { ok = false; break; }
                if (i == 0) path.MoveTo(sp); else path.LineTo(sp);
            }
            if (!ok) { path.Dispose(); continue; }
            path.Close();

            float lam = Math.Clamp(Dot(Norm(n), light), 0f, 1f) * 0.7f + 0.35f;
            using var paint = new SKPaint
            {
                Color = Scale(baseColor, lam),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            canvas.DrawPath(path, paint);

            using var edge = new SKPaint
            {
                Color = Darken(baseColor, 0.45f).WithAlpha(90),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1
            };
            canvas.DrawPath(path, edge);
            path.Dispose();
        }
    }

    private static void DrawAvatar(SKCanvas canvas, NearbyAvatar av, ProjectFn project)
    {
        var feet = new Vector3(av.Position.X, av.Position.Y, av.Position.Z - 0.95f);
        var head = new Vector3(av.Position.X, av.Position.Y, av.Position.Z + 0.95f);
        if (!project(feet, out var pf, out var d1)) return;
        if (!project(head, out var ph, out _)) return;

        float width = MathF.Max(3f, 260f / MathF.Max(d1, 0.5f));

        using var body = new SKPaint
        {
            Color = new SKColor(0xE8, 0x6A, 0xC8),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawLine(pf, ph, body);

        using var headPaint = new SKPaint { Color = new SKColor(0xFF, 0xC8, 0xE8), IsAntialias = true };
        canvas.DrawCircle(ph.X, ph.Y - width * 0.5f, width * 0.55f, headPaint);

        using var tag = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 26,
            TextAlign = SKTextAlign.Center
        };
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 190),
            IsAntialias = true,
            TextSize = 26,
            TextAlign = SKTextAlign.Center,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4
        };
        float ty = ph.Y - width * 1.2f - 10;
        canvas.DrawText(av.Name, ph.X, ty, shadow);
        canvas.DrawText(av.Name, ph.X, ty, tag);
    }

    private static void DrawSelf(SKCanvas canvas, Vector3 self, ProjectFn project)
    {
        var feet = new Vector3(self.X, self.Y, self.Z - 0.95f);
        var head = new Vector3(self.X, self.Y, self.Z + 0.95f);
        if (!project(feet, out var pf, out var d) || !project(head, out var ph, out _)) return;

        float width = MathF.Max(4f, 260f / MathF.Max(d, 0.5f));
        using var body = new SKPaint
        {
            Color = new SKColor(0x4F, 0xC3, 0xF7),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawLine(pf, ph, body);
        using var hp = new SKPaint { Color = new SKColor(0xB3, 0xE5, 0xFC), IsAntialias = true };
        canvas.DrawCircle(ph.X, ph.Y - width * 0.5f, width * 0.55f, hp);
    }

    private static void DrawGroundGrid(SKCanvas canvas, Vector3 self, ProjectFn project, float radius)
    {
        using var grid = new SKPaint
        {
            Color = new SKColor(0x3A, 0x55, 0x6B, 130),
            IsAntialias = true,
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke
        };

        float z = self.Z - 1.0f;
        float step = 4f;
        float r = MathF.Min(radius, 64f);
        float x0 = MathF.Floor((self.X - r) / step) * step;
        float y0 = MathF.Floor((self.Y - r) / step) * step;

        for (float x = x0; x <= self.X + r; x += step)
        {
            if (project(new Vector3(x, self.Y - r, z), out var a, out _) &&
                project(new Vector3(x, self.Y + r, z), out var b, out _))
                canvas.DrawLine(a, b, grid);
        }
        for (float y = y0; y <= self.Y + r; y += step)
        {
            if (project(new Vector3(self.X - r, y, z), out var a, out _) &&
                project(new Vector3(self.X + r, y, z), out var b, out _))
                canvas.DrawLine(a, b, grid);
        }
    }

    // ---------------- minimap + hud ----------------

    private static void DrawMinimap(SKCanvas canvas, int w, int h, Vector3 self,
        List<Primitive> prims, List<NearbyAvatar> avatars, float radius)
    {
        float size = MathF.Min(w, h) * 0.30f;
        size = Math.Clamp(size, 110f, 240f);
        float pad = 12f;
        float left = w - size - pad, top = pad;
        float cx = left + size / 2, cy = top + size / 2;
        float rpx = size / 2 - 4;
        float sc = rpx / MathF.Max(radius, 1f);

        using (var bg = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true })
            canvas.DrawCircle(cx, cy, rpx + 3, bg);
        using (var ring = new SKPaint
        {
            Color = new SKColor(0x7F, 0xB8, 0xE8, 180), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 2
        })
        {
            canvas.DrawCircle(cx, cy, rpx + 3, ring);
            canvas.DrawCircle(cx, cy, rpx * 0.5f, ring);
        }

        // north-up: +Y (north) is screen up, +X (east) is screen right
        using var primPaint = new SKPaint { Color = new SKColor(0x9E, 0xB3, 0xC4, 220), IsAntialias = false };
        foreach (var p in prims)
        {
            float dx = (p.Position.X - self.X) * sc;
            float dy = (p.Position.Y - self.Y) * sc;
            if (dx * dx + dy * dy > rpx * rpx) continue;
            canvas.DrawRect(cx + dx - 1.2f, cy - dy - 1.2f, 2.4f, 2.4f, primPaint);
        }

        using var avPaint = new SKPaint { Color = new SKColor(0xE8, 0x6A, 0xC8), IsAntialias = true };
        foreach (var a in avatars)
        {
            float dx = (a.Position.X - self.X) * sc;
            float dy = (a.Position.Y - self.Y) * sc;
            if (dx * dx + dy * dy > rpx * rpx) continue;
            canvas.DrawCircle(cx + dx, cy - dy, 3.5f, avPaint);
        }

        using var me = new SKPaint { Color = new SKColor(0x4F, 0xC3, 0xF7), IsAntialias = true };
        canvas.DrawCircle(cx, cy, 4f, me);

        using var nPaint = new SKPaint
        {
            Color = SKColors.White, IsAntialias = true, TextSize = 20, TextAlign = SKTextAlign.Center
        };
        canvas.DrawText("N", cx, top + 18, nPaint);
    }

    private static void DrawHud(SKCanvas canvas, int w, int h, Vector3 self, int prims, int avatars)
    {
        using var t = new SKPaint { Color = new SKColor(0xD8, 0xE4, 0xF0), IsAntialias = true, TextSize = 24 };
        canvas.DrawText($"<{self.X:0}, {self.Y:0}, {self.Z:0}>   objects {prims}   people {avatars}", 14, h - 16, t);
    }

    private static void DrawCenterText(SKCanvas canvas, int w, int h, string text)
    {
        using var t = new SKPaint
        {
            Color = SKColors.Gray, IsAntialias = true, TextSize = 30, TextAlign = SKTextAlign.Center
        };
        canvas.DrawText(text, w / 2f, h / 2f, t);
    }

    // ---------------- math + colour helpers ----------------

    private delegate bool ProjectFn(Vector3 p, out SKPoint s, out float depth);

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

    private static Vector3 Cross(Vector3 a, Vector3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

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
                return new SKColor(
                    (byte)Math.Clamp(c.R * 255f, 0, 255),
                    (byte)Math.Clamp(c.G * 255f, 0, 255),
                    (byte)Math.Clamp(c.B * 255f, 0, 255));
            }
        }
        catch { }

        uint id = prim.LocalID;
        return new SKColor(
            (byte)(120 + (id * 37) % 110),
            (byte)(120 + (id * 61) % 110),
            (byte)(120 + (id * 97) % 110));
    }

    private static SKColor Scale(SKColor c, float f) => new(
        (byte)Math.Clamp(c.Red * f, 0, 255),
        (byte)Math.Clamp(c.Green * f, 0, 255),
        (byte)Math.Clamp(c.Blue * f, 0, 255));

    private static SKColor Lighten(SKColor c, float f) => Scale(c, f);
    private static SKColor Darken(SKColor c, float f) => Scale(c, f);
}
