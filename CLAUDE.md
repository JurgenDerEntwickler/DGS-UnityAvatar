# DGS-UnityAvatar – Unity client

Unity project for the DGS avatar. It receives DGS gloss sequences from the
Python backend (repo `JurgenDerEntwickler/DGSNEW`) and plays one sign animation
per gloss. On the Mac the project is `~/Desktop/DGS-UnityAvatar/Signbot`
(open it in Unity Hub via "Add project from disk").

## Website (main use)

The avatar runs as a Unity WebGL build inside the DGSNEW website (`Start.command` there).

- Build: menu **DGS > Build WebGL for website** (asks for the DGSNEW folder once), or with
  the Editor closed:
  `Unity -batchmode -quit -projectPath <this project> -buildTarget WebGL -executeMethod WebGLBuilder.Build -buildPath <DGSNEW>/web/unity`
  (`Assets/Editor/WebGLBuilder.cs`: gzip + decompression fallback -> `Build/avatar.*.unityweb`, ~13 MB, ~2 min).
  Needs the "WebGL Build Support" module for 2022.3.62f3 in Unity Hub.
- In WebGL there is no TCP and no thread: the page calls
  `unityInstance.SendMessage(<GameObject>, "OnSignMessage", json)`.
- Unity -> page via `Assets/Plugins/WebGL/SignBridge.jslib`: `SignBridge_Ready(name)` on Start,
  `SignBridge_SignStarted(id)` per sign (id set by the page), `SignBridge_QueueEmpty()`.
- `WebGLInput.captureAllKeyboardInput = false`, so the page's text field gets keyboard input.

## How it works (Editor / desktop)

- `Assets/Scripts/SignTcpClient.cs` connects to `127.0.0.1:8765` and retries every
  `reconnectDelay` (2 s) until `backend/live_sign_avatar.py` is running, also after
  the server restarts. Start order of Python and Play does not matter.
- It reads ONE JSON message per line, puts every sign into a queue and plays
  them one after another: fire the trigger, wait until the Animator leaves
  idle and returns to `Breathing Idle`, then the next sign.
- Signs with `has_animation: false` fire no trigger:
  - `fingerspell: true` (word without a sign): wait `fingerspellSecondsPerLetter`
    (0.12 s) per letter, at most `maxFingerspellSeconds` (2 s); logged with ✋.
    Placeholder until a fingerspelling clip exists.
  - otherwise: short pause (`missingSignPause`, 0.3 s) so the sentence rhythm stays.
- Inspector settings: `animator`, `host`, `port`, `idleStateName`,
  `missingSignPause`, `gapBetweenSigns`, `maxSecondsPerSign`,
  `fingerspellSecondsPerLetter`, `maxFingerspellSeconds`, `reconnectDelay`.
- `UnityMainThreadDispatcher.cs` is no longer used by the client (kept, harmless).

## Message format (from Python)

```json
{"original_text": "...", "glosses": ["HALLO", "DANKE"],
 "signs": [{"gloss": "HALLO", "animation": "hallo", "has_animation": true,
            "fingerspell": false, "duration": 1.0}]}
```

Mapped by `SignMessage` / `SignEntry` in `SignTcpClient.cs`. If you change
fields, change `live_sign_avatar.py` in DGSNEW too. Python sends every gloss,
also words without a lexicon entry (`fingerspell: true`; numbers `false`).

## Assets

- Avatar: Mixamo `Ch23_nonPBR.fbx` (53 MB) with textures `Ch23_*`.
  No face blendshapes, so no facial expressions yet.
- Clips in `Assets/MaximoClips/`: `Breathing Idle`, `Waving` (used for HALLO), `danke`.
- Animator: `Assets/SignBotController.controller`, default state `Breathing Idle`.
  Trigger `hallo` -> state `Waving`; trigger `danke` -> state
  `Screen_Recording_2025-12-03_at_00_19_31` (the danke clip; rename it in the
  Animator when convenient). Every sign state must transition back to idle.

## Adding a new sign

1. Import the clip and add a state in `SignBotController`.
2. Add a Trigger parameter named exactly like the gloss in lowercase
   (`VERSTEHEN` -> `verstehen`; umlauts written out: `KOENNEN` -> `koennen`).
3. Transition idle -> sign on that trigger, sign -> idle with exit time.
4. In DGSNEW add the gloss to `ANIMATED` in `backend/build_dictionary.py`
   and run it, so Python sends `has_animation: true`.

## Known issues / plans

- Hand goes into the head in `danke`: the clip was retargeted from another body.
  Fix with Animation Rigging IK targets (chin, chest, forehead) or re-animate.
- Clips came from video via an online video-to-FBX tool; fingers are poor.
  Plan: keyframed signs from building blocks (handshape + location +
  orientation + movement), later motion capture.
- Better avatar with face rig under consideration: MetaHuman, Character Creator 4, VRoid.

## Git

- `Library/`, `Temp/`, `Logs/`, `UserSettings/` are ignored; never commit them.
- Keep files under 100 MB (GitHub limit); the avatar FBX is 53 MB.
