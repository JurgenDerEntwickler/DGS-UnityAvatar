using System.Collections.Generic;
using UnityEngine;

/// Face for Rocketbox "_facial" avatars: natural blinking plus smooth facial expressions
/// through the FACS blendshapes (AU_01_InnerBrowRaiser, AU_04_BrowLowerer, ...) and the
/// ARKit ones (AK_25_JawOpen, ...).
///
/// Put it on the avatar's root GameObject. Other scripts call
///     face.Set("AU_04_BrowLowerer", 0.8f);   // 0..1, fades in smoothly
///     face.Clear();                          // everything back to neutral
/// In DGS the face is grammar: W-questions -> brows down (AU_04), yes/no questions ->
/// brows up (AU_01 + AU_02). Right-click the component title for test expressions.
public class RocketboxFace : MonoBehaviour
{
    [Header("Blinking")]
    public bool autoBlink = true;
    [Tooltip("Random time between two blinks (seconds).")]
    public Vector2 blinkInterval = new Vector2(2.5f, 6f);
    [Range(0.05f, 0.4f)] public float blinkDuration = 0.15f;
    public string blinkShape = "AU_45_Blink";

    [Header("Expressions")]
    [Tooltip("How fast expressions fade in/out (higher = faster).")]
    [Range(1f, 30f)] public float expressionSpeed = 8f;

    struct Binding { public SkinnedMeshRenderer renderer; public int index; }

    readonly Dictionary<string, List<Binding>> shapes = new Dictionary<string, List<Binding>>();
    readonly Dictionary<string, float> target = new Dictionary<string, float>();
    readonly Dictionary<string, float> current = new Dictionary<string, float>();
    float nextBlink, blinkTime = -1f;

    void Awake()
    {
        foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = smr.sharedMesh;
            if (mesh == null) continue;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                // Unity may prefix the name with the mesh name ("mesh.AU_45_Blink").
                string full = mesh.GetBlendShapeName(i);
                string name = full.Substring(full.LastIndexOf('.') + 1);
                if (!shapes.TryGetValue(name, out var list)) shapes[name] = list = new List<Binding>();
                list.Add(new Binding { renderer = smr, index = i });
            }
        }
        Debug.Log($"RocketboxFace: {shapes.Count} blendshapes found" +
                  (shapes.Count == 0 ? " - did you use the *_facial.fbx with Import BlendShapes on?" : "."));
        nextBlink = Random.Range(blinkInterval.x, blinkInterval.y);
    }

    public bool Has(string shape) => shapes.ContainsKey(shape);
    public IEnumerable<string> ShapeNames => shapes.Keys;

    /// weight 0..1. Unknown names are ignored (logged once).
    public void Set(string shape, float weight)
    {
        if (!shapes.ContainsKey(shape)) { Debug.LogWarning($"RocketboxFace: no blendshape '{shape}'"); return; }
        target[shape] = Mathf.Clamp01(weight);
        if (!current.ContainsKey(shape)) current[shape] = 0f;
    }

    public void Clear()
    {
        foreach (var k in new List<string>(target.Keys)) target[k] = 0f;
    }

    // LateUpdate: runs after the animation, so nothing overwrites the face.
    void LateUpdate()
    {
        float dt = Time.deltaTime;
        float k = 1f - Mathf.Exp(-expressionSpeed * dt);   // frame-rate independent smoothing

        foreach (var name in new List<string>(current.Keys))
        {
            current[name] = Mathf.Lerp(current[name], target[name], k);
            Apply(name, current[name]);
        }

        if (autoBlink && Has(blinkShape))
        {
            float blink = 0f;
            if (blinkTime >= 0f)
            {
                blinkTime += dt;
                float x = blinkTime / blinkDuration;              // 0..1: close then open
                blink = x < 1f ? Mathf.Sin(x * Mathf.PI) : 0f;
                if (x >= 1f) { blinkTime = -1f; nextBlink = Random.Range(blinkInterval.x, blinkInterval.y); }
            }
            else if ((nextBlink -= dt) <= 0f) blinkTime = 0f;

            float baseW = current.TryGetValue(blinkShape, out var w) ? w : 0f;
            Apply(blinkShape, Mathf.Max(baseW, blink));
        }
    }

    void Apply(string name, float weight01)
    {
        foreach (var b in shapes[name]) b.renderer.SetBlendShapeWeight(b.index, weight01 * 100f);
    }

    [ContextMenu("Test: W-question (brows down)")]
    void TestWQuestion() { Clear(); Set("AU_04_BrowLowerer", 0.8f); }

    [ContextMenu("Test: yes/no question (brows up)")]
    void TestYesNo() { Clear(); Set("AU_01_InnerBrowRaiser", 0.8f); Set("AU_02_OuterBrowRaiser", 0.8f); }

    [ContextMenu("Test: smile")]
    void TestSmile() { Clear(); Set("AU_12_LipCornerPuller", 0.7f); Set("AU_06_CheekRaiser", 0.4f); }

    [ContextMenu("Test: neutral")]
    void TestNeutral() => Clear();

    [ContextMenu("Log all blendshape names")]
    void LogNames() => Debug.Log(string.Join(", ", shapes.Keys));
}
