using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.Rendering;
using SkiaSharp;

namespace SLMobileViewer.Services;

/// <summary>A triangle in avatar-local space (avatar origin, unrotated).</summary>
public struct LocalTri
{
    public Vector3 A, B, C;
    public Vector3 Normal;
    public SKColor Color;
}

public sealed class BakedAvatar
{
    public UUID Id { get; init; }
    public string Name { get; set; } = "";
    public volatile bool Ready;
    public LocalTri[] Tris = Array.Empty<LocalTri>();
    public bool HasMesh => Tris.Length > 0;
}

/// <summary>
/// Builds avatar geometry from their rigged mesh attachments (the mesh bodies,
/// heads, hair and clothing that modern avatars actually wear).
///
/// Rigged mesh is stored in bind pose, so for a resting pose the full skinning
/// equation collapses to just the bind-shape matrix — which means we get a
/// correct-looking avatar without needing joint matrices or animation data.
/// </summary>
public sealed class AvatarBaker
{
    private const int MaxAvatars = 8;
    private const int MaxTrisPerAvatar = 3500;

    private readonly GridClient _client;
    private readonly TextureTintCache _tints;
    private readonly MeshFoundry _renderer = new();
    private readonly ConcurrentDictionary<UUID, BakedAvatar> _avatars = new();
    private readonly ConcurrentDictionary<UUID, byte> _inFlight = new();
    private CancellationTokenSource? _cts;

    public event Action<string>? Progress;

    public AvatarBaker(GridClient client, TextureTintCache tints)
    {
        _client = client;
        _tints = tints;
    }

    public BakedAvatar? Get(UUID id) => _avatars.TryGetValue(id, out var a) ? a : null;

    public void Clear()
    {
        _cts?.Cancel();
        _avatars.Clear();
        _inFlight.Clear();
    }

    /// <summary>Bake (or refresh) the avatars currently near the camera.</summary>
    public void BakeNearby(Vector3 selfPos, float radius)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        Task.Run(() => BakeLoop(selfPos, radius, cts.Token), cts.Token);
    }

    private async Task BakeLoop(Vector3 selfPos, float radius, CancellationToken token)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return;

        var targets = sim.ObjectsAvatars.Values
            .Where(a => Vector3.Distance(selfPos, a.Position) <= MathF.Max(radius, 40f))
            .OrderBy(a => Vector3.Distance(selfPos, a.Position))
            .Take(MaxAvatars)
            .ToList();

        int i = 0;
        foreach (var av in targets)
        {
            if (token.IsCancellationRequested) return;
            i++;

            if (_avatars.TryGetValue(av.ID, out var existing) && existing.Ready) continue;
            if (!_inFlight.TryAdd(av.ID, 0)) continue;

            Report($"Loading avatar {i}/{targets.Count}…");
            try
            {
                var baked = await BuildAvatarAsync(av, sim, token).ConfigureAwait(false);
                _avatars[av.ID] = baked;
            }
            catch { }
            finally { _inFlight.TryRemove(av.ID, out _); }
        }

        Report(targets.Count == 0 ? "" : "Avatars loaded");
    }

    private async Task<BakedAvatar> BuildAvatarAsync(Avatar av, Simulator sim, CancellationToken token)
    {
        var baked = new BakedAvatar
        {
            Id = av.ID,
            Name = string.IsNullOrEmpty(av.Name) ? "Resident" : av.Name
        };

        // Attachments are primitives parented to the avatar.
        var attachments = sim.ObjectsPrimitives.Values
            .Where(p => p.ParentID == av.LocalID)
            .ToList();

        var tris = new List<LocalTri>();
        int meshOk = 0, meshFail = 0, attachTotal = attachments.Count;

        foreach (var prim in attachments)
        {
            if (token.IsCancellationRequested) break;
            if (tris.Count >= MaxTrisPerAvatar) break;

            var sculpt = prim.Sculpt;
            if (sculpt == null || sculpt.SculptTexture == UUID.Zero) continue;
            if ((sculpt.Type & (SculptType)0x3F) != SculptType.Mesh) continue;

            FacetedMesh? mesh = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(15000);
                var asset = await _client.Assets.RequestMeshAsync(sculpt.SculptTexture, timeout.Token)
                                                .ConfigureAwait(false);
                if (asset == null) continue;
                if (!FacetedMesh.TryDecodeFromAsset(prim, asset, DetailLevel.Low, out mesh) || mesh == null)
                { meshFail++; continue; }
                meshOk++;
            }
            catch { meshFail++; continue; }

            bool rigged = mesh.SkinData != null;
            float[] bind = mesh.SkinData?.BindShapeMatrix ?? Identity();

            foreach (var face in mesh.Faces)
            {
                if (face.Vertices == null || face.Indices == null) continue;
                if (tris.Count >= MaxTrisPerAvatar) break;

                var col = await FaceTintAsync(face, token).ConfigureAwait(false);

                for (int k = 0; k + 2 < face.Indices.Count && tris.Count < MaxTrisPerAvatar; k += 3)
                {
                    var a = LocalPos(face.Vertices[face.Indices[k]].Position, prim, rigged, bind);
                    var b = LocalPos(face.Vertices[face.Indices[k + 1]].Position, prim, rigged, bind);
                    var c = LocalPos(face.Vertices[face.Indices[k + 2]].Position, prim, rigged, bind);

                    tris.Add(new LocalTri
                    {
                        A = a, B = b, C = c,
                        Normal = Norm(Cross(Sub(b, a), Sub(c, a))),
                        Color = col
                    });
                }
            }
        }

        baked.Tris = tris.ToArray();
        baked.Ready = true;
        Report($"{baked.Name}: {meshOk}/{attachTotal} parts, {tris.Count} tris" +
               (meshFail > 0 ? $" ({meshFail} failed)" : ""));
        return baked;
    }

    /// <summary>
    /// Rigged mesh: bind-shape only (rest pose). Unrigged attachment: apply the
    /// prim's own scale/rotation/offset relative to the avatar.
    /// </summary>
    private static Vector3 LocalPos(Vector3 v, Primitive prim, bool rigged, float[] bind)
    {
        if (rigged)
        {
            var t = MatMul(bind, v);
            return t;
        }

        var s = new Vector3(v.X * prim.Scale.X, v.Y * prim.Scale.Y, v.Z * prim.Scale.Z);
        var r = Rotate(s, prim.Rotation);
        return new Vector3(prim.Position.X + r.X, prim.Position.Y + r.Y, prim.Position.Z + r.Z);
    }

    private async Task<SKColor> FaceTintAsync(Face face, CancellationToken token)
    {
        SKColor baseCol = new(190, 185, 180);
        try
        {
            var te = face.TextureFace;
            if (te != null)
            {
                var rgba = te.RGBA;
                baseCol = new SKColor(
                    (byte)Math.Clamp(rgba.R * 255f, 0, 255),
                    (byte)Math.Clamp(rgba.G * 255f, 0, 255),
                    (byte)Math.Clamp(rgba.B * 255f, 0, 255));

                var tint = await _tints.GetTintAsync(te.TextureID, token).ConfigureAwait(false);
                if (tint != null)
                {
                    // modulate texture average by the face tint
                    var t = tint.Value;
                    baseCol = new SKColor(
                        (byte)(t.Red * baseCol.Red / 255),
                        (byte)(t.Green * baseCol.Green / 255),
                        (byte)(t.Blue * baseCol.Blue / 255));
                }
            }
        }
        catch { }
        return baseCol;
    }

    private void Report(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
        MainThread.BeginInvokeOnMainThread(() => Progress?.Invoke(msg));
    }

    // ---- math ----
    private static float[] Identity() => new float[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };

    private static Vector3 MatMul(float[] m, Vector3 v)
    {
        if (m.Length < 16) return v;
        return new Vector3(
            m[0] * v.X + m[4] * v.Y + m[8] * v.Z + m[12],
            m[1] * v.X + m[5] * v.Y + m[9] * v.Z + m[13],
            m[2] * v.X + m[6] * v.Y + m[10] * v.Z + m[14]);
    }

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
