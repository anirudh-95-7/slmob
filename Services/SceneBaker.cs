using System.Diagnostics;
using LibreMetaverse;
using LibreMetaverse.Rendering;
using SkiaSharp;

namespace SLMobileViewer.Services;

/// <summary>One world-space triangle, pre-transformed at bake time.</summary>
public struct BakedTri
{
    public Vector3 A, B, C;
    public Vector3 Normal;
    public Vector3 Centroid;
    public SKColor Color;
    public uint LocalId;
}

public sealed record BakeProgress(int Done, int Total, int Triangles, bool Finished, string Message);

/// <summary>
/// Builds a static, world-space triangle scene from nearby primitives, in small
/// background batches. The scene is a snapshot: it does NOT follow the avatar.
/// The user rebuilds or extends it explicitly.
/// </summary>
public sealed class SceneBaker
{
    private readonly GridClient _client;
    private readonly SpatialCullEngine _cull;
    private readonly SimpleRenderer _renderer = new();

    private volatile BakedTri[] _tris = Array.Empty<BakedTri>();
    private CancellationTokenSource? _cts;
    private readonly HashSet<uint> _baked = new();
    private readonly List<BakedTri> _accum = new();
    private readonly object _lock = new();

    /// <summary>Triangle budget for software rasterisation on a phone.</summary>
    public int TriangleBudget { get; set; } = 14000;

    public float BakedRadius { get; private set; }
    public Vector3 Origin { get; private set; }
    public bool HasScene => _tris.Length > 0;
    public bool IsBaking { get; private set; }

    public BakedTri[] Triangles => _tris;

    public event Action<BakeProgress>? Progress;

    public SceneBaker(GridClient client, SpatialCullEngine cull)
    {
        _client = client;
        _cull = cull;
    }

    public void Clear()
    {
        _cts?.Cancel();
        lock (_lock) { _accum.Clear(); _baked.Clear(); }
        _tris = Array.Empty<BakedTri>();
        BakedRadius = 0;
    }

    /// <summary>Distance the avatar has drifted from the baked scene's origin.</summary>
    public float DriftFromOrigin()
        => HasScene ? Vector3.Distance(_cull.AvatarPosition(), Origin) : 0f;

    /// <summary>Bake from scratch at the current position.</summary>
    public void Rebuild(float radius)
    {
        Clear();
        Origin = _cull.AvatarPosition();
        Bake(radius, fresh: true);
    }

    /// <summary>Extend the existing bake outwards, keeping what is already built.</summary>
    public void Extend(float radius)
    {
        if (!HasScene) { Rebuild(radius); return; }
        Bake(radius, fresh: false);
    }

    private void Bake(float radius, bool fresh)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        _cull.CullRadius = MathF.Max(_cull.CullRadius, radius);
        BakedRadius = radius;
        IsBaking = true;

        Task.Run(() =>
        {
            try { BakeLoop(radius, token); }
            catch (Exception ex) { Report(0, 0, "Bake failed: " + ex.Message, true); }
            finally { IsBaking = false; }
        }, token);
    }

    private void BakeLoop(float radius, CancellationToken token)
    {
        var origin = Origin;
        var prims = _cull.SnapshotPrims()
            .Where(p => Vector3.Distance(origin, p.Position) <= radius)
            .OrderBy(p => Vector3.Distance(origin, p.Position))
            .ToList();

        int total = prims.Count, done = 0;
        var sw = Stopwatch.StartNew();

        foreach (var prim in prims)
        {
            if (token.IsCancellationRequested) return;
            done++;

            bool already;
            lock (_lock) already = !_baked.Add(prim.LocalID);
            if (already) continue;

            int triCount;
            lock (_lock) triCount = _accum.Count;
            if (triCount >= TriangleBudget)
            {
                Publish();
                Report(done, total, $"Detail budget reached — {triCount:N0} triangles", true);
                return;
            }

            var tris = BuildPrim(prim, origin);
            lock (_lock) _accum.AddRange(tris);

            // publish partial results so the world appears bit by bit
            if (sw.ElapsedMilliseconds > 350)
            {
                sw.Restart();
                Publish();
                lock (_lock) triCount = _accum.Count;
                Report(done, total, $"Building scene… {done}/{total}", false);
            }

            // stay friendly to the UI thread and the battery
            if (done % 12 == 0) Thread.Sleep(12);
        }

        Publish();
        int final;
        lock (_lock) final = _accum.Count;
        Report(total, total, $"Scene ready — {total} objects, {final:N0} triangles", true);
    }

    private void Publish()
    {
        lock (_lock) _tris = _accum.ToArray();
    }

    private void Report(int done, int total, string msg, bool finished)
    {
        int tris = _tris.Length;
        MainThread.BeginInvokeOnMainThread(() =>
            Progress?.Invoke(new BakeProgress(done, total, tris, finished, msg)));
    }

    // ---------------- geometry ----------------

    private List<BakedTri> BuildPrim(Primitive prim, Vector3 origin)
    {
        var result = new List<BakedTri>();
        float size = MathF.Max(prim.Scale.X, MathF.Max(prim.Scale.Y, prim.Scale.Z));
        float dist = Vector3.Distance(origin, prim.Position);

        // Level of detail by physical size and distance — keeps the budget honest.
        var lod = (size > 6f && dist < 20f) ? DetailLevel.Medium : DetailLevel.Low;

        FacetedMesh? mesh = null;
        bool isSculptOrMesh = prim.Sculpt != null;
        if (!isSculptOrMesh)
        {
            try { mesh = _renderer.GenerateFacetedMesh(prim, lod); }
            catch { mesh = null; }
        }

        if (mesh == null || mesh.Faces.Count == 0)
            return BoxFallback(prim);

        foreach (var face in mesh.Faces)
        {
            if (face.Vertices == null || face.Indices == null) continue;

            SKColor col = FaceColor(face, prim);
            var verts = face.Vertices;
            var idx = face.Indices;

            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                var a = ToWorld(verts[idx[i]].Position, prim);
                var b = ToWorld(verts[idx[i + 1]].Position, prim);
                var c = ToWorld(verts[idx[i + 2]].Position, prim);

                var n = Norm(Cross(Sub(b, a), Sub(c, a)));
                result.Add(new BakedTri
                {
                    A = a, B = b, C = c,
                    Normal = n,
                    Centroid = new Vector3((a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f, (a.Z + b.Z + c.Z) / 3f),
                    Color = col,
                    LocalId = prim.LocalID
                });
            }
        }
        return result;
    }

    private static Vector3 ToWorld(Vector3 local, Primitive prim)
    {
        var s = new Vector3(local.X * prim.Scale.X, local.Y * prim.Scale.Y, local.Z * prim.Scale.Z);
        var r = Rotate(s, prim.Rotation);
        return new Vector3(prim.Position.X + r.X, prim.Position.Y + r.Y, prim.Position.Z + r.Z);
    }

    private static List<BakedTri> BoxFallback(Primitive prim)
    {
        var res = new List<BakedTri>();
        float hx = prim.Scale.X * .5f, hy = prim.Scale.Y * .5f, hz = prim.Scale.Z * .5f;
        var local = new[]
        {
            new Vector3(-hx,-hy,-hz), new Vector3(hx,-hy,-hz), new Vector3(hx,hy,-hz), new Vector3(-hx,hy,-hz),
            new Vector3(-hx,-hy, hz), new Vector3(hx,-hy, hz), new Vector3(hx,hy, hz), new Vector3(-hx,hy, hz)
        };
        var w = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var r = Rotate(local[i], prim.Rotation);
            w[i] = new Vector3(prim.Position.X + r.X, prim.Position.Y + r.Y, prim.Position.Z + r.Z);
        }
        int[][] quads = { new[]{0,1,2,3}, new[]{4,5,6,7}, new[]{0,1,5,4}, new[]{1,2,6,5}, new[]{2,3,7,6}, new[]{3,0,4,7} };
        var col = PrimColor(prim);

        foreach (var q in quads)
        {
            AddTri(res, w[q[0]], w[q[1]], w[q[2]], col, prim.LocalID);
            AddTri(res, w[q[0]], w[q[2]], w[q[3]], col, prim.LocalID);
        }
        return res;
    }

    private static void AddTri(List<BakedTri> list, Vector3 a, Vector3 b, Vector3 c, SKColor col, uint id)
    {
        var n = Norm(Cross(Sub(b, a), Sub(c, a)));
        list.Add(new BakedTri
        {
            A = a, B = b, C = c, Normal = n,
            Centroid = new Vector3((a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f, (a.Z + b.Z + c.Z) / 3f),
            Color = col, LocalId = id
        });
    }

    private static SKColor FaceColor(Face face, Primitive prim)
    {
        try
        {
            var rgba = face.TextureFace?.RGBA;
            if (rgba != null)
            {
                var c = rgba.Value;
                var col = new SKColor(
                    (byte)Math.Clamp(c.R * 255f, 0, 255),
                    (byte)Math.Clamp(c.G * 255f, 0, 255),
                    (byte)Math.Clamp(c.B * 255f, 0, 255));
                if (col.Red > 240 && col.Green > 240 && col.Blue > 240) return HashColor(prim.LocalID);
                return col;
            }
        }
        catch { }
        return PrimColor(prim);
    }

    private static SKColor PrimColor(Primitive prim)
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
                if (col.Red > 240 && col.Green > 240 && col.Blue > 240) return HashColor(prim.LocalID);
                return col;
            }
        }
        catch { }
        return HashColor(prim.LocalID);
    }

    private static SKColor HashColor(uint id) => new(
        (byte)(105 + (id * 37) % 125),
        (byte)(105 + (id * 61) % 125),
        (byte)(105 + (id * 97) % 125));

    private static Vector3 Sub(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Vector3 Cross(Vector3 a, Vector3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Vector3 Norm(Vector3 v)
    {
        float m = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return m < 1e-6f ? new Vector3(0, 0, 1) : new Vector3(v.X / m, v.Y / m, v.Z / m);
    }

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
}
