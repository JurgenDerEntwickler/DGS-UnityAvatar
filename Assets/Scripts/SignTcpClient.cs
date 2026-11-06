using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
#if !UNITY_WEBGL || UNITY_EDITOR
using System.Net.Sockets;
#endif

/// Receives sign sequences and plays them ONE AFTER ANOTHER.
/// Every word of a sentence gets its own turn: trigger -> wait until the clip has
/// finished and the avatar is back in idle -> next sign. New sentences are appended
/// to the same queue, so nothing is skipped.
///
/// Two ways in, same queue:
///   * Editor / desktop build: TCP 127.0.0.1:8765 from backend/live_sign_avatar.py
///     (or web_app.py --unity-editor), reconnecting automatically.
///   * WebGL build in the website: the page calls OnSignMessage(json) via SendMessage
///     and gets SignStarted / QueueEmpty back (Assets/Plugins/WebGL/SignBridge.jslib).
public class SignTcpClient : MonoBehaviour
{
    public Animator animator;
    public string host = "127.0.0.1";
    public int port = 8765;

    [Tooltip("Name of the idle state in the Animator (the state every sign returns to).")]
    public string idleStateName = "Breathing Idle";

    [Tooltip("Fallback time for signs without animation when the message has no duration (Python sends one).")]
    public float missingSignPause = 1.2f;

    [Tooltip("Short gap between two signs.")]
    public float gapBetweenSigns = 0.2f;

    [Tooltip("Safety limit: never wait longer than this for one sign.")]
    public float maxSecondsPerSign = 10f;

    [Tooltip("Time per letter for words without a sign (fingerspell=true), until a fingerspelling clip exists.")]
    public float fingerspellSecondsPerLetter = 0.3f;

    [Tooltip("Upper limit for one fingerspelled word.")]
    public float maxFingerspellSeconds = 4f;

    [Tooltip("Seconds between connection attempts while the Python server is not running.")]
    public float reconnectDelay = 2f;

    [Tooltip("Smooth sign-to-sign player. Empty = taken from this GameObject, or added automatically " +
             "when Assets/Resources/SignLibrary.asset exists. Without it, the Animator triggers are used.")]
    public SmoothSignPlayer smoothPlayer;

    volatile bool running;

    // Filled by the network thread or the web page, emptied by the main thread.
    readonly ConcurrentQueue<SignEntry> signQueue = new ConcurrentQueue<SignEntry>();
    int idleHash;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void SignBridge_Ready(string receiverName);
    [DllImport("__Internal")] static extern void SignBridge_SignStarted(int id);
    [DllImport("__Internal")] static extern void SignBridge_QueueEmpty();
#else
    static void SignBridge_Ready(string receiverName) { }
    static void SignBridge_SignStarted(int id) { }
    static void SignBridge_QueueEmpty() { }

    TcpClient client;
    Thread listenerThread;
#endif

    void Start()
    {
        idleHash = Animator.StringToHash(idleStateName);
        running = true;

        if (smoothPlayer == null) smoothPlayer = GetComponent<SmoothSignPlayer>();
        if (smoothPlayer == null && Resources.Load<SignLibrary>("SignLibrary") != null)
            smoothPlayer = gameObject.AddComponent<SmoothSignPlayer>();
        if (smoothPlayer != null && !smoothPlayer.Init(animator)) smoothPlayer = null;
        Debug.Log(smoothPlayer != null ? "Sign playback: smooth (SignLibrary)" : "Sign playback: Animator triggers");
#if UNITY_WEBGL && !UNITY_EDITOR
        WebGLInput.captureAllKeyboardInput = false;   // let the page's text field get Enter/typing
#endif
#if !UNITY_WEBGL || UNITY_EDITOR
        listenerThread = new Thread(ListenForMessages) { IsBackground = true };
        listenerThread.Start();
#endif
        StartCoroutine(PlayQueue());
        SignBridge_Ready(gameObject.name);   // web page: "send me sentences now"
    }

    // ---------- web page (WebGL): unityInstance.SendMessage(name, "OnSignMessage", json) ----------
    public void OnSignMessage(string json) => Enqueue(json);

    void Enqueue(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        SignMessage msg;
        try { msg = JsonUtility.FromJson<SignMessage>(json); }
        catch (Exception e) { Debug.LogWarning("Bad JSON: " + e.Message); return; }
        if (msg?.signs == null) return;

        Debug.Log($"Received sentence: \"{msg.original_text}\" ({msg.signs.Length} signs)");
        foreach (var sign in msg.signs)
            signQueue.Enqueue(sign);
    }

#if !UNITY_WEBGL || UNITY_EDITOR
    // ---------- network thread (Editor / desktop) ----------
    // Keeps trying to connect, so Play can be pressed before or after starting Python,
    // and a restarted Python server is picked up again automatically.
    void ListenForMessages()
    {
        bool warned = false;
        while (running)
        {
            try
            {
                client = new TcpClient(host, port);
                Debug.Log("Connected to Python server.");
                warned = false;

                // Python sends one JSON object per line ("\n"), so read line by line.
                // This also handles two sentences arriving at once or one arriving in pieces.
                using (var reader = new StreamReader(client.GetStream(), Encoding.UTF8))
                {
                    string line;
                    while (running && (line = reader.ReadLine()) != null)
                        Enqueue(line);
                }
                if (running) Debug.LogWarning("Python server closed the connection, reconnecting ...");
            }
            catch (Exception e)
            {
                if (running && !warned)
                {
                    Debug.LogWarning($"Python server not reachable ({e.Message}), retrying every {reconnectDelay}s ...");
                    warned = true;
                }
            }
            finally
            {
                client?.Close();
                client = null;
            }
            if (running) Thread.Sleep((int)(reconnectDelay * 1000));
        }
    }
#endif

    // ---------- main thread ----------
    IEnumerator PlayQueue()
    {
        bool busy = false;
        while (true)
        {
            if (!signQueue.TryDequeue(out var sign))
            {
                if (busy) { busy = false; SignBridge_QueueEmpty(); }
                yield return null;
                continue;
            }
            busy = true;
            SignBridge_SignStarted(sign.id);

            if (smoothPlayer != null)
            {
                yield return PlaySmooth(sign);
                continue;   // no fixed gap: the blends do the spacing
            }

            if (sign.has_animation && HasTrigger(sign.animation))
            {
                Debug.Log($"▶ {sign.gloss} ({sign.animation})");
                yield return PlaySign(sign.animation);
            }
            else if (sign.fingerspell)
            {
                // Word without a sign: placeholder for fingerspelling. Python sends the time
                // (grows with the word length); the Inspector values are only a fallback.
                float t = sign.duration > 0 ? sign.duration
                    : Mathf.Min(sign.gloss.Length * fingerspellSecondsPerLetter, maxFingerspellSeconds);
                Debug.Log($"✋ {sign.gloss} (fingerspell, {t:0.00}s)");
                yield return new WaitForSeconds(t);
            }
            else
            {
                // No clip yet: don't fire a trigger, just keep the timing of a real sign.
                float t = sign.duration > 0 ? sign.duration : missingSignPause;
                Debug.Log($"· {sign.gloss} (no animation yet, {t:0.00}s)");
                yield return new WaitForSeconds(t);
            }

            yield return new WaitForSeconds(gapBetweenSigns);
        }
    }

    // The library decides what has a clip (not has_animation), so a new sign only needs
    // a library entry in Unity; Python's ANIMATED list no longer has to match.
    IEnumerator PlaySmooth(SignEntry sign)
    {
        Func<int> waiting = () => signQueue.Count;
        if (smoothPlayer.HasSign(sign))
        {
            Debug.Log($"▶ {sign.gloss} (#{sign.sign_id})");
            yield return smoothPlayer.PlaySign(sign, waiting);
        }
        else if (sign.fingerspell)
        {
            float t = sign.duration > 0 ? sign.duration
                : Mathf.Min(sign.gloss.Length * fingerspellSecondsPerLetter, maxFingerspellSeconds);
            Debug.Log($"✋ {sign.gloss} (fingerspell)");
            yield return smoothPlayer.Fingerspell(sign.gloss, t, waiting);
        }
        else
        {
            float t = sign.duration > 0 ? sign.duration : missingSignPause;
            Debug.Log($"· {sign.gloss} (no clip, {t:0.00}s)");
            yield return smoothPlayer.Pause(t, waiting);
        }
    }

    void Update()
    {
        if (smoothPlayer != null) smoothPlayer.Backlog = signQueue.Count;
    }

    IEnumerator PlaySign(string trigger)
    {
        animator.ResetTrigger(trigger);
        animator.SetTrigger(trigger);
        float t = 0f;

        // 1) wait until the avatar has left idle (the clip has started)
        while (InIdle() && t < 0.5f) { t += Time.deltaTime; yield return null; }

        // 2) wait until the clip is done and the avatar is back in idle
        while (!InIdle() && t < maxSecondsPerSign) { t += Time.deltaTime; yield return null; }
    }

    bool InIdle()
    {
        return !animator.IsInTransition(0) &&
               animator.GetCurrentAnimatorStateInfo(0).shortNameHash == idleHash;
    }

    bool HasTrigger(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var p in animator.parameters)
            if (p.type == AnimatorControllerParameterType.Trigger && p.name == name)
                return true;
        return false;
    }

    void OnApplicationQuit()
    {
        running = false;
#if !UNITY_WEBGL || UNITY_EDITOR
        client?.Close();
#endif
    }
}

[Serializable]
public class SignMessage
{
    public string original_text;
    public string[] glosses;
    public SignEntry[] signs;
}

[Serializable]
public class SignEntry
{
    public int id;               // set by the web page, reported back in SignStarted (0 over TCP)
    public int sign_id;          // optional: the sign's number in the SignLibrary (0 = look up by gloss)
    public string gloss;
    public string animation;
    public bool has_animation;
    public bool fingerspell;
    public float duration;       // seconds for a sign WITHOUT clip (from Python); clips use their own length
}
