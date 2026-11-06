using System;
using System.Collections.Generic;
using UnityEngine;

/// All signs the avatar knows, each with a NUMBER (id), its gloss and its clip.
///
/// This is the "word -> number -> movement" table on the Unity side:
///   * Python can send `sign_id` (preferred, the number) or just the gloss/animation name.
///   * Each entry also stores where the sign really starts and ends inside its clip
///     (inPoint / outPoint, 0..1). The part before inPoint (hands rising from rest) and
///     after outPoint (hands dropping back) is skipped, so signs flow into each other.
///
/// Keep the asset at Assets/Resources/SignLibrary.asset: SignTcpClient then finds it on
/// its own (no scene change). Create / fill it with menu DGS > Sign Library Tools.
[CreateAssetMenu(menuName = "DGS/Sign Library", fileName = "SignLibrary")]
public class SignLibrary : ScriptableObject
{
    [Tooltip("Rest pose between sentences (looped).")]
    public AnimationClip idleClip;

    [Tooltip("Optional: pose held for words without a clip (e.g. hands ready in front of the chest). Empty = idle.")]
    public AnimationClip neutralClip;

    public List<SignClip> signs = new List<SignClip>();

    [Serializable]
    public class SignClip
    {
        [Tooltip("The sign's number. Start at 1; 0 means 'no number'.")]
        public int id;

        [Tooltip("Gloss in UPPERCASE, umlauts written out (KOENNEN). Fingerspelling letters: FS_A, FS_B, ...")]
        public string gloss;

        public AnimationClip clip;

        [Tooltip("Where the sign starts inside the clip (0..1). Skips the hands rising from rest.")]
        [Range(0f, 1f)] public float inPoint = 0f;

        [Tooltip("Where the sign ends inside the clip (0..1). Skips the hands dropping back to rest.")]
        [Range(0f, 1f)] public float outPoint = 1f;

        [Tooltip("Playback speed of this sign (1 = as recorded).")]
        [Range(0.5f, 2f)] public float speed = 1f;

        [Tooltip("Original clip before smoothing (set by the smoothing tool, used to re-smooth).")]
        public AnimationClip sourceClip;
    }

    Dictionary<int, SignClip> byId;
    Dictionary<string, SignClip> byName;

    void OnEnable() => Rebuild();
    void OnValidate() => Rebuild();

    public void Rebuild()
    {
        byId = new Dictionary<int, SignClip>();
        byName = new Dictionary<string, SignClip>();
        foreach (var s in signs)
        {
            if (s == null || s.clip == null) continue;
            if (s.id > 0 && !byId.ContainsKey(s.id)) byId[s.id] = s;
            var key = Key(s.gloss);
            if (key.Length > 0 && !byName.ContainsKey(key)) byName[key] = s;
        }
    }

    /// Number first, then gloss, then the animation name (lowercase gloss).
    public SignClip Find(int id, string gloss, string animation)
    {
        if (byId == null) Rebuild();
        if (id > 0 && byId.TryGetValue(id, out var e)) return e;
        if (byName.TryGetValue(Key(gloss), out e)) return e;
        if (byName.TryGetValue(Key(animation), out e)) return e;
        return null;
    }

    public static string Key(string s) => string.IsNullOrEmpty(s) ? "" : s.Trim().ToUpperInvariant();
}
