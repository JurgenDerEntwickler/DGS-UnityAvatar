using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// Menu DGS > Sign Library Tools.
///
///   1. Create / import  : builds Assets/Resources/SignLibrary.asset from the existing
///                         Animator Controller (trigger -> clip) or from clips selected in
///                         the Project window. Every sign gets the next free number.
///   2. Auto-trim        : finds where each sign really starts/ends (arms leave / return to
///                         the rest pose) and sets inPoint / outPoint.
///   3. Smooth           : removes jitter from video-to-FBX clips with a Savitzky-Golay filter
///                         (keeps peaks and contacts better than a plain blur). Writes
///                         smoothed copies to Assets/SignClipsSmoothed/, originals are kept.
///   4. Export ids       : writes sign_ids.json (id, gloss) for the Python word -> number table.
public class SignLibraryTools : EditorWindow
{
    const string LibraryPath = "Assets/Resources/SignLibrary.asset";
    const string SmoothFolder = "Assets/SignClipsSmoothed";
    const string DefaultController = "Assets/SignBotController.controller";

    SignLibrary library;
    AnimatorController controller;
    string idleStateName = "Breathing Idle";

    float trimThreshold = 0.25f;
    int trimMarginFrames = 3;

    int smoothHalfWindow = 3;     // frames at 60 fps -> window of 7 frames (~0.12 s)
    int smoothPasses = 1;
    bool smoothFingers = true;
    Vector2 scroll;

    [MenuItem("DGS/Sign Library Tools")]
    static void Open() => GetWindow<SignLibraryTools>("DGS Signs");

    void OnEnable()
    {
        library = AssetDatabase.LoadAssetAtPath<SignLibrary>(LibraryPath);
        controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(DefaultController);
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        library = (SignLibrary)EditorGUILayout.ObjectField("Sign Library", library, typeof(SignLibrary), false);
        if (library == null && GUILayout.Button("Create " + LibraryPath)) library = GetOrCreateLibrary();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("1. Import", EditorStyles.boldLabel);
        controller = (AnimatorController)EditorGUILayout.ObjectField("Animator Controller", controller, typeof(AnimatorController), false);
        idleStateName = EditorGUILayout.TextField("Idle state", idleStateName);
        using (new EditorGUI.DisabledScope(controller == null))
            if (GUILayout.Button("Import signs from Animator Controller")) ImportFromController();
        if (GUILayout.Button("Add clips selected in the Project window (gloss = clip name)")) AddSelectedClips();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("2. Auto-trim in/out points", EditorStyles.boldLabel);
        trimThreshold = EditorGUILayout.Slider(new GUIContent("Threshold",
            "Share of the sign's largest arm movement away from rest that counts as 'signing'."), trimThreshold, 0.05f, 0.9f);
        trimMarginFrames = EditorGUILayout.IntSlider(new GUIContent("Margin (frames @30fps)"), trimMarginFrames, 0, 15);
        using (new EditorGUI.DisabledScope(library == null || library.idleClip == null))
            if (GUILayout.Button("Auto-trim all signs")) AutoTrimAll();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("3. Smooth clips (remove jitter)", EditorStyles.boldLabel);
        smoothHalfWindow = EditorGUILayout.IntSlider(new GUIContent("Half window (frames @60fps)",
            "2-3 = light (keeps fast taps), 5-8 = strong (video jitter)."), smoothHalfWindow, 1, 12);
        smoothPasses = EditorGUILayout.IntSlider("Passes", smoothPasses, 1, 4);
        smoothFingers = EditorGUILayout.Toggle(new GUIContent("Smooth fingers too",
            "Off = only body/arms; use when handshapes change quickly."), smoothFingers);
        using (new EditorGUI.DisabledScope(library == null))
            if (GUILayout.Button("Smooth all signs (from original clips)")) SmoothAll();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("4. Python", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(library == null))
            if (GUILayout.Button("Export sign_ids.json")) ExportIds();

        EditorGUILayout.Space();
        if (library != null) DrawWarnings();
        EditorGUILayout.EndScrollView();
    }

    void DrawWarnings()
    {
        var problems = new List<string>();
        if (library.idleClip == null) problems.Add("No idle clip set.");
        foreach (var g in library.signs.Where(s => s.id > 0).GroupBy(s => s.id).Where(g => g.Count() > 1))
            problems.Add($"Number {g.Key} is used {g.Count()} times.");
        foreach (var g in library.signs.GroupBy(s => SignLibrary.Key(s.gloss)).Where(g => g.Count() > 1))
            problems.Add($"Gloss {g.Key} appears {g.Count()} times.");
        foreach (var s in library.signs.Where(s => s.clip == null)) problems.Add($"{s.gloss}: no clip.");
        EditorGUILayout.HelpBox(problems.Count == 0
            ? $"{library.signs.Count} signs, no problems."
            : string.Join("\n", problems), problems.Count == 0 ? MessageType.Info : MessageType.Warning);
    }

    // ------------------------------------------------------------ 1. import

    static SignLibrary GetOrCreateLibrary()
    {
        var lib = AssetDatabase.LoadAssetAtPath<SignLibrary>(LibraryPath);
        if (lib != null) return lib;
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        lib = CreateInstance<SignLibrary>();
        AssetDatabase.CreateAsset(lib, LibraryPath);
        AssetDatabase.SaveAssets();
        return lib;
    }

    static int NextId(SignLibrary lib) => lib.signs.Count == 0 ? 1 : Math.Max(1, lib.signs.Max(s => s.id) + 1);

    static bool AddSign(SignLibrary lib, string gloss, AnimationClip clip)
    {
        gloss = SignLibrary.Key(gloss);
        if (clip == null || gloss.Length == 0) return false;
        if (lib.signs.Any(s => SignLibrary.Key(s.gloss) == gloss)) return false;
        lib.signs.Add(new SignLibrary.SignClip { id = NextId(lib), gloss = gloss, clip = clip });
        return true;
    }

    void ImportFromController()
    {
        library = library != null ? library : GetOrCreateLibrary();
        Undo.RecordObject(library, "Import signs");
        var sm = controller.layers[0].stateMachine;
        var triggers = new HashSet<string>(controller.parameters
            .Where(p => p.type == AnimatorControllerParameterType.Trigger).Select(p => p.name));

        foreach (var cs in sm.states)
            if (cs.state.name == idleStateName || (library.idleClip == null && cs.state == sm.defaultState))
                library.idleClip = cs.state.motion as AnimationClip;

        int added = 0;
        void Scan(IEnumerable<AnimatorStateTransition> ts)
        {
            foreach (var t in ts)
                foreach (var c in t.conditions)
                    if (triggers.Contains(c.parameter) && AddSign(library, c.parameter, t.destinationState?.motion as AnimationClip))
                        added++;
        }
        Scan(sm.anyStateTransitions);
        foreach (var cs in sm.states) Scan(cs.state.transitions);

        Save();
        Debug.Log($"Imported {added} signs from {controller.name}. Idle: {(library.idleClip ? library.idleClip.name : "none")}.");
    }

    void AddSelectedClips()
    {
        library = library != null ? library : GetOrCreateLibrary();
        Undo.RecordObject(library, "Add clips");
        int added = 0;
        foreach (var clip in SelectedClips())
            if (AddSign(library, clip.name, clip)) added++;
        Save();
        Debug.Log($"Added {added} clips. Rename the gloss in the library if the clip name is not the gloss.");
    }

    /// Clips selected directly, plus the clips inside selected FBX files.
    static IEnumerable<AnimationClip> SelectedClips()
    {
        foreach (var obj in Selection.objects)
        {
            if (obj is AnimationClip c && !c.name.StartsWith("__preview__")) { yield return c; continue; }
            var path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) continue;
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>())
                if (!sub.name.StartsWith("__preview__")) yield return sub;
        }
    }

    // ------------------------------------------------------------ 2. auto-trim

    /// Arm/shoulder muscles (not fingers, not IK goals) decide where signing starts.
    static bool IsArmMuscle(string name) =>
        (name.Contains("Arm") || name.Contains("Shoulder") || (name.Contains("Hand") && !name.Contains("Hand.")))
        && !name.Contains("HandT") && !name.Contains("HandQ");

    void AutoTrimAll()
    {
        Undo.RecordObject(library, "Auto-trim");
        var rest = new Dictionary<string, float>();
        foreach (var b in AnimationUtility.GetCurveBindings(library.idleClip))
            if (b.type == typeof(Animator) && IsArmMuscle(b.propertyName))
                rest[b.propertyName] = AnimationUtility.GetEditorCurve(library.idleClip, b).Evaluate(0f);
        if (rest.Count == 0)
        {
            Debug.LogWarning("Idle clip has no humanoid arm muscle curves; is the rig set to Humanoid?");
            return;
        }

        foreach (var s in library.signs)
        {
            var clip = s.clip;
            if (clip == null || SignLibrary.Key(s.gloss).StartsWith("FS_")) continue;   // letters are static
            var curves = AnimationUtility.GetCurveBindings(clip)
                .Where(b => b.type == typeof(Animator) && rest.ContainsKey(b.propertyName))
                .Select(b => (rest[b.propertyName], AnimationUtility.GetEditorCurve(clip, b))).ToList();
            if (curves.Count == 0) continue;

            int n = Mathf.Max(2, Mathf.RoundToInt(clip.length * 30f) + 1);
            var dev = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = clip.length * i / (n - 1);
                foreach (var (r, c) in curves) dev[i] = Mathf.Max(dev[i], Mathf.Abs(c.Evaluate(t) - r));
            }
            float max = dev.Max();
            if (max < 0.05f) continue;                       // barely moves: leave it
            float thr = trimThreshold * max;
            int first = Array.FindIndex(dev, d => d >= thr);
            int last = Array.FindLastIndex(dev, d => d >= thr);

            float inP = dev[0] < thr ? Mathf.Max(0, first - trimMarginFrames) / (float)(n - 1) : 0f;
            float outP = dev[n - 1] < thr ? Mathf.Min(n - 1, last + trimMarginFrames) / (float)(n - 1) : 1f;
            if (outP - inP < 0.2f) continue;                 // would cut too much: leave it
            s.inPoint = inP;
            s.outPoint = outP;
            Debug.Log($"{s.gloss}: in {inP * clip.length:0.00}s, out {outP * clip.length:0.00}s of {clip.length:0.00}s");
        }
        Save();
    }

    // ------------------------------------------------------------ 3. smoothing

    void SmoothAll()
    {
        Undo.RecordObject(library, "Smooth clips");
        if (!AssetDatabase.IsValidFolder(SmoothFolder)) AssetDatabase.CreateFolder("Assets", "SignClipsSmoothed");
        int done = 0;
        foreach (var s in library.signs)
        {
            var src = s.sourceClip != null ? s.sourceClip : s.clip;
            if (src == null) continue;
            var path = $"{SmoothFolder}/{Safe(SignLibrary.Key(s.gloss))}.anim";
            s.sourceClip = src;
            s.clip = SmoothClip(src, path, smoothHalfWindow, smoothPasses, smoothFingers);
            done++;
        }
        Save();
        Debug.Log($"Smoothed {done} clips into {SmoothFolder} (originals kept as sourceClip).");
    }

    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    static AnimationClip SmoothClip(AnimationClip src, string path, int halfWindow, int passes, bool fingers)
    {
        const float fps = 60f;
        var dst = new AnimationClip { frameRate = fps };
        var bindings = AnimationUtility.GetCurveBindings(src);
        var outCurves = new AnimationCurve[bindings.Length];
        int n = Mathf.Max(2, Mathf.RoundToInt(src.length * fps) + 1);

        for (int b = 0; b < bindings.Length; b++)
        {
            var curve = AnimationUtility.GetEditorCurve(src, bindings[b]);
            var name = bindings[b].propertyName;
            bool isFinger = name.Contains("Hand.");          // LeftHand.Index.1 Stretched ...
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = curve.Evaluate(src.length * i / (n - 1));
            if (!isFinger || fingers)
                for (int p = 0; p < passes; p++) x = SavitzkyGolay(x, halfWindow);

            var keys = new Keyframe[n];
            for (int i = 0; i < n; i++) keys[i] = new Keyframe(src.length * i / (n - 1), x[i]);
            var c = new AnimationCurve(keys);
            for (int i = 0; i < n; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(c, i, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode(c, i, AnimationUtility.TangentMode.ClampedAuto);
            }
            outCurves[b] = c;
        }
        AnimationUtility.SetEditorCurves(dst, bindings, outCurves);

        var settings = AnimationUtility.GetAnimationClipSettings(src);
        settings.loopTime = false;
        AnimationUtility.SetAnimationClipSettings(dst, settings);
        AnimationUtility.SetAnimationEvents(dst, AnimationUtility.GetAnimationEvents(src));

        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (existing != null)
        {
            EditorUtility.CopySerialized(dst, existing);    // keep the GUID, references stay valid
            DestroyImmediate(dst);
            return existing;
        }
        AssetDatabase.CreateAsset(dst, path);
        return dst;
    }

    /// Quadratic Savitzky-Golay filter, window 2m+1, mirrored at the ends.
    /// c_k = 3(3m^2 + 3m - 1 - 5k^2) / ((2m-1)(2m+1)(2m+3)); m=2 gives (-3,12,17,12,-3)/35.
    static float[] SavitzkyGolay(float[] x, int m)
    {
        int n = x.Length;
        if (m < 1 || n < 3) return (float[])x.Clone();
        float denom = (2 * m - 1) * (2 * m + 1) * (2 * m + 3);
        var c = new float[2 * m + 1];
        for (int k = -m; k <= m; k++) c[k + m] = 3f * (3 * m * m + 3 * m - 1 - 5 * k * k) / denom;

        var y = new float[n];
        for (int i = 0; i < n; i++)
        {
            float sum = 0f;
            for (int k = -m; k <= m; k++)
            {
                int j = i + k;
                if (j < 0) j = -j;
                if (j > n - 1) j = 2 * (n - 1) - j;
                j = Mathf.Clamp(j, 0, n - 1);
                sum += c[k + m] * x[j];
            }
            y[i] = sum;
        }
        return y;
    }

    // ------------------------------------------------------------ 4. export

    [Serializable] class IdRow { public int id; public string gloss; }
    [Serializable] class IdTable { public List<IdRow> signs = new List<IdRow>(); }

    void ExportIds()
    {
        var path = EditorUtility.SaveFilePanel("Export sign ids", "", "sign_ids.json", "json");
        if (string.IsNullOrEmpty(path)) return;
        var table = new IdTable();
        foreach (var s in library.signs.OrderBy(s => s.id))
            table.signs.Add(new IdRow { id = s.id, gloss = SignLibrary.Key(s.gloss) });
        File.WriteAllText(path, JsonUtility.ToJson(table, true));
        Debug.Log($"Wrote {table.signs.Count} sign ids to {path}");
    }

    void Save()
    {
        library.Rebuild();
        EditorUtility.SetDirty(library);
        AssetDatabase.SaveAssets();
    }
}
