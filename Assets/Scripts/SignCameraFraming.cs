using UnityEngine;

/// Frames the camera on the signing space: from just below the waist up to fully raised
/// hands, wide enough for the arms. Legs are left out. Measured from the avatar's skeleton
/// (humanoid rig), so it also fits other avatars, and it re-fits whenever the window/canvas
/// aspect changes (browser resize, phone).
///
/// Added to the main camera automatically at start (no scene change needed). To tweak the
/// values, add this component to the Main Camera yourself; then that one is used.
[RequireComponent(typeof(Camera))]
public class SignCameraFraming : MonoBehaviour
{
    [Tooltip("Avatar to frame. Empty = the humanoid Animator of SignTcpClient / first humanoid in the scene.")]
    public Animator avatar;

    [Tooltip("Vertical field of view. Smaller = less perspective distortion of the hands.")]
    [Range(15f, 60f)] public float fieldOfView = 30f;

    [Tooltip("How far below the hips the frame ends at least (metres).")]
    public float belowHips = 0.05f;

    [Tooltip("Free space below the fingertips of the hanging hands at rest (metres).")]
    public float belowRestingHands = 0.06f;

    [Tooltip("Free space above the fully raised hand (metres).")]
    public float aboveRaisedHands = 0.2f;

    [Tooltip("Share of the arm length added left and right of the shoulders (1 = arms fully sideways).")]
    [Range(0.3f, 1.2f)] public float sideReach = 0.75f;

    [Tooltip("Extra margin around the frame (share of its size).")]
    [Range(0f, 0.3f)] public float padding = 0.04f;

    Camera cam;
    bool measured;
    float lastAspect;
    Vector3 center, forward;
    float halfHeight, halfWidth;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoAdd()
    {
        var main = Camera.main;
        if (main != null && main.GetComponent<SignCameraFraming>() == null)
            main.gameObject.AddComponent<SignCameraFraming>();
    }

    void Awake() => cam = GetComponent<Camera>();

    // LateUpdate: the Animator has posed the avatar (idle) before we measure it.
    void LateUpdate()
    {
        if (!measured)
        {
            if (!Measure()) return;
            measured = true;
            Apply();
        }
        else if (!Mathf.Approximately(cam.aspect, lastAspect))
        {
            Apply();
        }
    }

    Animator FindAvatar()
    {
        if (avatar != null) return avatar;
        var client = FindObjectOfType<SignTcpClient>();
        if (client != null && client.animator != null && client.animator.isHuman) return client.animator;
        foreach (var a in FindObjectsOfType<Animator>())
            if (a.isHuman) return a;
        return null;
    }

    bool Measure()
    {
        var a = FindAvatar();
        if (a == null) { enabled = false; Debug.LogWarning("SignCameraFraming: no humanoid avatar found."); return false; }

        Transform B(HumanBodyBones b) => a.GetBoneTransform(b);
        var hips = B(HumanBodyBones.Hips);
        var head = B(HumanBodyBones.Head);
        var lShoulder = B(HumanBodyBones.LeftUpperArm);
        var rShoulder = B(HumanBodyBones.RightUpperArm);
        var lElbow = B(HumanBodyBones.LeftLowerArm);
        var lHand = B(HumanBodyBones.LeftHand);
        var rHand = B(HumanBodyBones.RightHand);
        if (hips == null || head == null || lShoulder == null || rShoulder == null || lElbow == null
            || lHand == null || rHand == null)
        {
            enabled = false;
            Debug.LogWarning("SignCameraFraming: avatar is missing humanoid bones.");
            return false;
        }

        // Arm length shoulder -> elbow -> wrist, plus ~ a hand length.
        float upper = Vector3.Distance(lShoulder.position, lElbow.position);
        float lower = Vector3.Distance(lElbow.position, lHand.position);
        float arm = upper + lower + lower * 0.6f;   // + hand and fingers

        float shoulderY = (lShoulder.position.y + rShoulder.position.y) * 0.5f;
        // Hanging hands at rest (arms straight down, fingertips) must stay in view. Computed from
        // shoulder height and arm length, not from the current pose: in the first frame the avatar
        // may still be in its T-pose. If the hands already hang lower, use that.
        float restingFingertips = Mathf.Min(shoulderY - arm,
            Mathf.Min(lHand.position.y, rHand.position.y) - lower * 0.6f);
        float bottom = Mathf.Min(hips.position.y - belowHips, restingFingertips - belowRestingHands);
        float top = Mathf.Max(shoulderY + arm, head.position.y + 0.15f) + aboveRaisedHands;

        float shoulderHalf = Vector3.Distance(lShoulder.position, rShoulder.position) * 0.5f;
        halfWidth = (shoulderHalf + arm * sideReach) * (1f + padding);
        halfHeight = (top - bottom) * 0.5f * (1f + padding);

        var root = a.transform;
        Vector3 bodyCenter = new Vector3(hips.position.x, 0f, hips.position.z);
        center = bodyCenter + Vector3.up * (bottom + top) * 0.5f;
        forward = Vector3.ProjectOnPlane(root.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.5f) forward = Vector3.forward;
        return true;
    }

    void Apply()
    {
        lastAspect = cam.aspect;
        cam.fieldOfView = fieldOfView;
        float tanV = Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
        float distance = Mathf.Max(halfHeight / tanV, halfWidth / (tanV * cam.aspect));
        cam.nearClipPlane = Mathf.Min(cam.nearClipPlane, distance * 0.5f);
        transform.position = center + forward * distance;
        transform.rotation = Quaternion.LookRotation(-forward, Vector3.up);
    }
}
