using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// Receives sign sequences from the Python server and plays them ONE AFTER ANOTHER.
/// Every word of a sentence gets its own turn: trigger -> wait until the clip has
/// finished and the avatar is back in idle -> next sign. New sentences are appended
/// to the same queue, so nothing is skipped.
public class SignTcpClient : MonoBehaviour
{
    public Animator animator;
    public string host = "127.0.0.1";
    public int port = 8765;

    [Tooltip("Name of the idle state in the Animator (the state every sign returns to).")]
    public string idleStateName = "Breathing Idle";

    [Tooltip("Pause in seconds for signs that have no animation yet (keeps the rhythm of the sentence).")]
    public float missingSignPause = 0.3f;

    [Tooltip("Short gap between two signs.")]
    public float gapBetweenSigns = 0.1f;

    [Tooltip("Safety limit: never wait longer than this for one sign.")]
    public float maxSecondsPerSign = 10f;

    [Tooltip("Time per letter for words without a sign (fingerspell=true), until a fingerspelling clip exists.")]
    public float fingerspellSecondsPerLetter = 0.12f;

    [Tooltip("Upper limit for one fingerspelled word.")]
    public float maxFingerspellSeconds = 2f;

    [Tooltip("Seconds between connection attempts while the Python server is not running.")]
    public float reconnectDelay = 2f;

    TcpClient client;
    Thread listenerThread;
    volatile bool running;

    // Filled by the network thread, emptied by the main thread.
    readonly ConcurrentQueue<SignEntry> signQueue = new ConcurrentQueue<SignEntry>();
    int idleHash;

    void Start()
    {
        idleHash = Animator.StringToHash(idleStateName);
        running = true;
        listenerThread = new Thread(ListenForMessages) { IsBackground = true };
        listenerThread.Start();
        StartCoroutine(PlayQueue());
    }

    // ---------- network thread ----------
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
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        SignMessage msg;
                        try { msg = JsonUtility.FromJson<SignMessage>(line); }
                        catch (Exception e) { Debug.LogWarning("Bad JSON: " + e.Message); continue; }
                        if (msg?.signs == null) continue;

                        Debug.Log($"Received sentence: \"{msg.original_text}\" ({msg.signs.Length} signs)");
                        foreach (var sign in msg.signs)
                            signQueue.Enqueue(sign);
                    }
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

    // ---------- main thread ----------
    IEnumerator PlayQueue()
    {
        while (true)
        {
            if (!signQueue.TryDequeue(out var sign))
            {
                yield return null;
                continue;
            }

            if (sign.has_animation && HasTrigger(sign.animation))
            {
                Debug.Log($"▶ {sign.gloss} ({sign.animation})");
                yield return PlaySign(sign.animation);
            }
            else if (sign.fingerspell)
            {
                // Word without a sign: placeholder for fingerspelling, time grows with the word length.
                float t = Mathf.Min(sign.gloss.Length * fingerspellSecondsPerLetter, maxFingerspellSeconds);
                Debug.Log($"✋ {sign.gloss} (fingerspell, {t:0.00}s)");
                yield return new WaitForSeconds(t);
            }
            else
            {
                // No clip yet: don't fire a trigger, just keep the timing.
                Debug.Log($"· {sign.gloss} (no animation yet)");
                yield return new WaitForSeconds(missingSignPause);
            }

            yield return new WaitForSeconds(gapBetweenSigns);
        }
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
        client?.Close();
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
    public string gloss;
    public string animation;
    public bool has_animation;
    public bool fingerspell;
    public float duration;
}
