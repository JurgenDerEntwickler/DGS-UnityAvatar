using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// Plays signs so they FLOW into each other, the way a human signer does (coarticulation),
/// instead of dropping the hands back to rest after every word.
///
///   * Sign -> sign: crossfade straight from the end of one sign (its outPoint) into the
///     start of the next (its inPoint). The blend overlaps the end of the old sign.
///   * Only when nothing follows for `holdBeforeRest` seconds do the hands go back to idle.
///   * Blends use a smootherstep curve (no sudden acceleration at the start/end).
///   * Humanoid clips blend in muscle space, so the arms move along natural arcs.
///   * Tempo: if the lecturer is faster than the avatar and signs pile up in the queue,
///     playback speeds up gently (up to `maxTempo`) and slows back down when caught up.
///
/// Uses the Playables API, so it needs NO Animator Controller states/triggers per sign:
/// every sign is just an entry in the SignLibrary (number, gloss, clip, in/out points).
/// Works in WebGL. SignTcpClient adds this component itself when
/// Assets/Resources/SignLibrary.asset exists.
public class SmoothSignPlayer : MonoBehaviour
{
    public Animator animator;
    public SignLibrary library;

    [Header("Blending (seconds)")]
    [Tooltip("Blend from one sign into the next. Shorter = crisper, longer = softer. 0.12-0.25 looks natural.")]
    [Range(0.05f, 0.6f)] public float signToSignBlend = 0.18f;

    [Tooltip("Blend from rest (idle) into the first sign of a sentence.")]
    [Range(0.05f, 1f)] public float fromRestBlend = 0.3f;

    [Tooltip("Blend back to rest after the last sign.")]
    [Range(0.05f, 1.5f)] public float toRestBlend = 0.5f;

    [Tooltip("How long the last pose is held before going back to rest. The next sentence often comes within this time.")]
    [Range(0f, 3f)] public float holdBeforeRest = 0.7f;

    [Tooltip("Blend into the pause pose for words without a clip.")]
    [Range(0.05f, 1f)] public float pauseBlend = 0.35f;

    [Header("Tempo (catching up with the speaker)")]
    [Tooltip("Fastest playback when many signs are waiting. 1 = never speed up.")]
    [Range(1f, 2f)] public float maxTempo = 1.35f;

    [Tooltip("Number of waiting signs at which maxTempo is reached.")]
    [Min(2)] public int backlogForMaxTempo = 8;

    [Tooltip("How fast the tempo may change (per second), so it never jumps.")]
    [Range(0.05f, 2f)] public float tempoChangePerSecond = 0.4f;

    [Header("Fingerspelling")]
    [Tooltip("Letters are library entries named prefix + letter, e.g. FS_A. If one letter is missing, the word gets a pause instead.")]
    public string letterPrefix = "FS_";
    [Range(0.1f, 1f)] public float secondsPerLetter = 0.3f;
    [Range(0.02f, 0.3f)] public float letterBlend = 0.08f;

    /// Current playback tempo (1 = normal).
    public float Tempo { get; private set; } = 1f;

    /// Number of signs waiting in the queue (set by SignTcpClient every frame).
    public int Backlog { get; set; }

    public bool IsReady => ready;

    class Voice
    {
        public AnimationClipPlayable playable;
        public int slot;
        public double length;
        public double stopAt = -1;   // freeze here (the sign's outPoint), so it never drifts back to rest
        public bool loop;
        public float weight, from, to;
    }

    PlayableGraph graph;
    AnimationMixerPlayable mixer;
    readonly List<Voice> voices = new List<Voice>();
    Voice idle, current;
    bool fading;
    float fadeTime, fadeDuration;
    float holdTimer = -1f;   // >= 0 while the last sign holds its final pose
    bool ready;

    /// Builds the graph. Returns false (and the client falls back to Animator triggers)
    /// if there is no library / idle clip.
    public bool Init(Animator fallbackAnimator)
    {
        if (ready) return true;
        if (animator == null) animator = fallbackAnimator;
        if (library == null) library = Resources.Load<SignLibrary>("SignLibrary");
        if (animator == null || library == null || library.idleClip == null)
        {
            Debug.LogWarning("SmoothSignPlayer: no Animator, SignLibrary or idle clip -> using the Animator Controller instead.");
            return false;
        }
        library.Rebuild();

        // The graph drives the avatar from now on; the controller would fight it.
        animator.runtimeAnimatorController = null;
        animator.applyRootMotion = false;

        graph = PlayableGraph.Create("DGS Signs");
        graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
        mixer = AnimationMixerPlayable.Create(graph, 0);
        var output = AnimationPlayableOutput.Create(graph, "Signs", animator);
        output.SetSourcePlayable(mixer);

        idle = AddVoice(library.idleClip, true);
        SetWeight(idle, 1f);
        current = idle;

        graph.Play();
        ready = true;
        Debug.Log($"SmoothSignPlayer ready: {library.signs.Count} signs in the library.");
        return true;
    }

    void OnDestroy()
    {
        if (graph.IsValid()) graph.Destroy();
    }

    // ------------------------------------------------------------ public API

    public bool HasSign(SignEntry s) => ready && library.Find(s.sign_id, s.gloss, s.animation) != null;

    /// Plays one sign. Returns when the next sign should start (slightly BEFORE this one
    /// ends if another sign is waiting, so the two overlap).
    public IEnumerator PlaySign(SignEntry s, Func<int> waiting)
    {
        var e = library.Find(s.sign_id, s.gloss, s.animation);
        if (e == null) yield break;

        float blend = current == idle ? fromRestBlend : signToSignBlend;
        var v = StartClip(e, blend);

        // Start the next sign half a blend before this one's outPoint, so the middle
        // of the crossfade sits on the outPoint.
        float t = 0f, limit = (float)(v.stopAt / Math.Max(0.1, e.speed)) + 5f;
        while (t < limit)
        {
            if (current != v) yield break;                      // something else took over
            v.playable.SetSpeed(e.speed * Tempo);
            double lead = waiting() > 0 ? 0.5 * signToSignBlend * e.speed : 0.0;
            if (v.playable.GetTime() >= v.stopAt - lead) break;
            t += Time.deltaTime;
            yield return null;
        }

        if (waiting() == 0) holdTimer = 0f;   // hold the pose, rest later unless a sign comes
    }

    /// Word without a clip: blend to the pause pose (neutral or idle) and wait.
    public IEnumerator Pause(float seconds, Func<int> waiting)
    {
        holdTimer = -1f;
        if (library.neutralClip != null)
        {
            var v = AddVoice(library.neutralClip, true);
            FadeTo(v, pauseBlend / Tempo);
        }
        else if (current != idle)
        {
            FadeTo(idle, pauseBlend / Tempo);
        }
        yield return Wait(seconds);
        if (waiting() == 0) holdTimer = 0f;
    }

    /// Spells a word letter by letter if every letter has a clip (FS_A, FS_B, ...);
    /// otherwise falls back to Pause(fallbackSeconds).
    public IEnumerator Fingerspell(string word, float fallbackSeconds, Func<int> waiting)
    {
        var letters = new List<SignLibrary.SignClip>();
        foreach (char c in SignLibrary.Key(word))
        {
            if (!char.IsLetter(c)) continue;
            var e = library.Find(0, letterPrefix + c, null);
            if (e == null) { letters = null; break; }
            letters.Add(e);
        }
        if (letters == null || letters.Count == 0)
        {
            yield return Pause(fallbackSeconds, waiting);
            yield break;
        }

        holdTimer = -1f;
        for (int i = 0; i < letters.Count; i++)
        {
            float blend = i > 0 ? letterBlend : (current == idle ? fromRestBlend : signToSignBlend);
            StartClip(letters[i], blend);
            yield return Wait(secondsPerLetter + (i == 0 ? blend : 0f));
        }
        if (waiting() == 0) holdTimer = 0f;
    }

    // ------------------------------------------------------------ internals

    Voice StartClip(SignLibrary.SignClip e, float blendSeconds)
    {
        holdTimer = -1f;
        var v = AddVoice(e.clip, false);
        double len = Math.Max(0.01, v.length);
        double inT = Mathf.Clamp01(e.inPoint) * len;
        double outT = Mathf.Clamp(e.outPoint, e.inPoint, 1f) * len;
        if (outT - inT < 0.05) outT = Math.Min(len, inT + 0.05);
        v.stopAt = outT;
        v.playable.SetTime(inT);
        v.playable.SetSpeed(e.speed * Tempo);
        FadeTo(v, blendSeconds / Tempo);
        return v;
    }

    IEnumerator Wait(float seconds)
    {
        float t = 0f;
        while (t < seconds) { t += Time.deltaTime * Tempo; yield return null; }
    }

    Voice AddVoice(AnimationClip clip, bool loop)
    {
        var p = AnimationClipPlayable.Create(graph, clip);
        int slot = FreeSlot();
        graph.Connect(p, 0, mixer, slot);
        mixer.SetInputWeight(slot, 0f);
        var v = new Voice { playable = p, slot = slot, length = clip.length, loop = loop };
        voices.Add(v);
        return v;
    }

    int FreeSlot()
    {
        int n = mixer.GetInputCount();
        for (int i = 0; i < n; i++)
            if (!mixer.GetInput(i).IsValid()) return i;
        mixer.SetInputCount(n + 1);
        return n;
    }

    void RemoveVoice(Voice v)
    {
        graph.Disconnect(mixer, v.slot);
        if (v.playable.IsValid()) v.playable.Destroy();
        voices.Remove(v);
    }

    void SetWeight(Voice v, float w)
    {
        v.weight = w;
        mixer.SetInputWeight(v.slot, w);
    }

    /// Starts a crossfade: target goes to 1, everything else to 0. All weights move with
    /// the same curve, so they always add up to 1 (no pose "sagging" mid-blend).
    void FadeTo(Voice target, float seconds)
    {
        foreach (var v in voices) { v.from = v.weight; v.to = v == target ? 1f : 0f; }
        current = target;
        fadeTime = 0f;
        fadeDuration = Mathf.Max(0.0001f, seconds);
        fading = true;
    }

    static float Smootherstep(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * x * (x * (x * 6f - 15f) + 10f);
    }

    void Update()
    {
        if (!ready) return;
        float dt = Time.deltaTime;

        // Tempo follows the backlog, but changes slowly.
        float k = Mathf.Clamp01((Backlog - 1f) / Mathf.Max(1f, backlogForMaxTempo - 1f));
        Tempo = Mathf.MoveTowards(Tempo, Mathf.Lerp(1f, maxTempo, k), tempoChangePerSecond * dt);

        foreach (var v in voices)
        {
            double time = v.playable.GetTime();
            if (v.loop)
            {
                if (v.length > 0.01 && time >= v.length) v.playable.SetTime(time % v.length);
            }
            else if (v.stopAt >= 0 && time >= v.stopAt)
            {
                v.playable.SetSpeed(0);          // freeze on the sign's last real pose
                v.playable.SetTime(v.stopAt);
            }
        }

        if (holdTimer >= 0f)
        {
            holdTimer += dt;
            if (holdTimer >= holdBeforeRest)
            {
                holdTimer = -1f;
                if (current != idle) FadeTo(idle, toRestBlend);
            }
        }

        if (fading)
        {
            fadeTime += dt;
            float s = Smootherstep(fadeTime / fadeDuration);
            foreach (var v in voices) SetWeight(v, Mathf.Lerp(v.from, v.to, s));
            if (fadeTime >= fadeDuration)
            {
                fading = false;
                for (int i = voices.Count - 1; i >= 0; i--)
                {
                    var v = voices[i];
                    if (v != idle && v != current && v.weight <= 0.0001f) RemoveVoice(v);
                }
            }
        }
    }
}
