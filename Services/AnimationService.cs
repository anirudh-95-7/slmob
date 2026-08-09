using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.Animesh;
using LibreMetaverse.Rendering;

namespace SLMobileViewer.Services;

/// <summary>
/// Tracks which animations each avatar is playing, downloads and decodes the
/// .anim assets, and evaluates a blended joint pose.
///
/// LibreMetaverse already ships the decoder (BinBVHAnimationReader) and the
/// skinning maths (AnimeshSkinning); this class is the wiring between them.
/// </summary>
public sealed class AnimationService
{
    private sealed class Track
    {
        public UUID Id;
        public BinBVHAnimationReader? Data;
        public float Time;
    }

    private readonly GridClient _client;
    private readonly ConcurrentDictionary<UUID, List<Track>> _byAvatar = new();
    private readonly ConcurrentDictionary<UUID, BinBVHAnimationReader?> _assets = new();
    private readonly ConcurrentDictionary<UUID, byte> _downloading = new();

    public LindenSkeleton? Skeleton { get; private set; }
    public bool Ready => Skeleton != null;

    public AnimationService(GridClient client)
    {
        _client = client;

        _client.Avatars.AvatarAnimation += (s, e) =>
        {
            var ids = e.Animations?.Select(a => a.AnimationID).Distinct().ToList() ?? new List<UUID>();
            var list = new List<Track>();

            foreach (var id in ids)
            {
                if (id == UUID.Zero) continue;
                _assets.TryGetValue(id, out var data);
                list.Add(new Track { Id = id, Data = data });
                if (data == null) _ = EnsureAssetAsync(id);
            }
            _byAvatar[e.AvatarID] = list;
        };
    }

    /// <summary>
    /// The skeleton definition ships as @(Content) in the NuGet package, which the
    /// Android build drops, so we carry our own copy and extract it on first run.
    /// </summary>
    public async Task LoadSkeletonAsync()
    {
        if (Skeleton != null) return;
        try
        {
            var dest = Path.Combine(FileSystem.AppDataDirectory, "avatar_skeleton.xml");
            if (!File.Exists(dest))
            {
                using var src = await FileSystem.OpenAppPackageFileAsync("avatar_skeleton.xml");
                using var dst = File.Create(dest);
                await src.CopyToAsync(dst);
            }
            Skeleton = LindenSkeleton.Load(dest);
        }
        catch
        {
            try { Skeleton = LindenSkeleton.Load(); } catch { Skeleton = null; }
        }
    }

    private async Task EnsureAssetAsync(UUID id)
    {
        if (!_downloading.TryAdd(id, 0)) return;
        try
        {
            using var cts = new CancellationTokenSource(15000);
            var asset = await _client.Assets.RequestAssetAsync(id, AssetType.Animation, false, cts.Token)
                                            .ConfigureAwait(false);
            if (asset?.AssetData == null || asset.AssetData.Length == 0)
            {
                _assets[id] = null;
                return;
            }

            var reader = new BinBVHAnimationReader(asset.AssetData);
            _assets[id] = reader;

            foreach (var list in _byAvatar.Values)
                foreach (var t in list)
                    if (t.Id == id) t.Data = reader;
        }
        catch { _assets[id] = null; }
        finally { _downloading.TryRemove(id, out _); }
    }

    public void Advance(float dt)
    {
        foreach (var list in _byAvatar.Values)
            foreach (var t in list)
                if (t.Data != null) t.Time += dt;
    }

    /// <summary>Blended pose for one avatar, highest priority per joint wins.</summary>
    public Dictionary<string, JointPose>? PoseFor(UUID avatarId)
    {
        if (!_byAvatar.TryGetValue(avatarId, out var tracks) || tracks.Count == 0) return null;

        var pose = new Dictionary<string, JointPose>(StringComparer.Ordinal);
        bool any = false;

        foreach (var t in tracks)
        {
            var d = t.Data;
            if (d?.joints == null || d.joints.Length == 0) continue;
            any = true;

            float len = MathF.Max(d.Length, 0.01f);
            float time = d.Loop ? t.Time % len : MathF.Min(t.Time, len);

            foreach (var j in d.joints)
            {
                if (string.IsNullOrEmpty(j.Name)) continue;
                int priority = j.Priority != 0 ? j.Priority : d.Priority;

                if (pose.TryGetValue(j.Name, out var existing) && existing.Priority > priority)
                    continue;

                var jp = new JointPose { Priority = priority, EaseWeight = 1f };

                if (j.rotationkeys is { Length: > 0 })
                {
                    var v = Sample(j.rotationkeys, time);
                    jp.Rotation = FromCompressed(v);
                    jp.HasRotation = true;
                }
                if (j.positionkeys is { Length: > 0 })
                {
                    jp.Position = Sample(j.positionkeys, time);
                    jp.HasPosition = true;
                }

                if (jp.HasRotation || jp.HasPosition) pose[j.Name] = jp;
            }
        }
        return any && pose.Count > 0 ? pose : null;
    }

    public void Clear()
    {
        _byAvatar.Clear();
        _assets.Clear();
        _downloading.Clear();
    }

    /// <summary>Linear sample of a keyframe channel.</summary>
    private static Vector3 Sample(binBVHJointKey[] keys, float time)
    {
        if (keys.Length == 1) return keys[0].key_element;

        if (time <= keys[0].time) return keys[0].key_element;
        if (time >= keys[^1].time) return keys[^1].key_element;

        for (int i = 0; i < keys.Length - 1; i++)
        {
            var a = keys[i];
            var b = keys[i + 1];
            if (time < a.time || time > b.time) continue;

            float span = b.time - a.time;
            float f = span <= 1e-6f ? 0f : (time - a.time) / span;
            return new Vector3(
                a.key_element.X + (b.key_element.X - a.key_element.X) * f,
                a.key_element.Y + (b.key_element.Y - a.key_element.Y) * f,
                a.key_element.Z + (b.key_element.Z - a.key_element.Z) * f);
        }
        return keys[^1].key_element;
    }

    /// <summary>SL stores rotations as the X/Y/Z of a normalised quaternion.</summary>
    private static Quaternion FromCompressed(Vector3 v)
    {
        float sq = v.X * v.X + v.Y * v.Y + v.Z * v.Z;
        float w = sq < 1f ? MathF.Sqrt(1f - sq) : 0f;
        return new Quaternion(v.X, v.Y, v.Z, w);
    }
}
