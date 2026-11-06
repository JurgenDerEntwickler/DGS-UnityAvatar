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
- `Assets/Scripts/SignCameraFraming.cs` frames the camera on the signing space (adds itself to the
  main camera at start): from the fingertips of the hanging hands up to fully raised hands, arms
  in width, legs cut; 30° FOV; measured from the humanoid skeleton, re-fits on aspect change.
  Add the component to the Main Camera to tweak it in the Inspector.

## How it works (Editor / desktop)

- `Assets/Scripts/SignTcpClient.cs` connects to `127.0.0.1:8765` and retries every
  `reconnectDelay` (2 s) until `backend/live_sign_avatar.py` is running, also after
  the server restarts. Start order of Python and Play does not matter.
- It reads ONE JSON message per line, puts every sign into a queue and plays
  them one after another: fire the trigger, wait until the Animator leaves
  idle and returns to `Breathing Idle`, then the next sign.
- Signs with `has_animation: false` fire no trigger; they wait `duration` seconds,
  which Python sends (DGSNEW `live_sign_avatar.py`: 1.2 s per sign, fingerspelling
  0.3 s per letter within 1.2-4 s), so timing is tuned there without a rebuild:
  - `fingerspell: true` (word without a sign): logged with ✋, placeholder until a
    fingerspelling clip exists.
  - otherwise: logged with ·, keeps the rhythm of a real sign.
  - Inspector `missingSignPause` / `fingerspellSecondsPerLetter` / `maxFingerspellSeconds`
    are only used if a message has no `duration`. `gapBetweenSigns` = 0.2 s.
- Inspector settings: `animator`, `host`, `port`, `idleStateName`,
  `missingSignPause`, `gapBetweenSigns`, `maxSecondsPerSign`,
  `fingerspellSecondsPerLetter`, `maxFingerspellSeconds`, `reconnectDelay`.
- `UnityMainThreadDispatcher.cs` is no longer used by the client (kept, harmless).

## Smooth playback (SignLibrary + SmoothSignPlayer)

Used automatically when `Assets/Resources/SignLibrary.asset` exists; otherwise the
Animator triggers above are used (old path, unchanged).

- `SignLibrary` (ScriptableObject): one entry per sign = `id` (number, from 1), `gloss`,
  `clip`, `inPoint`/`outPoint` (0..1, where the sign really starts/ends in the clip),
  `speed`, `sourceClip` (original before smoothing). Plus `idleClip` and optional `neutralClip`
  (pose held for words without a clip). Lookup: `sign_id` first, then gloss, then animation name.
- `SmoothSignPlayer` (Playables, no Animator states needed): crossfades sign -> sign directly
  (`signToSignBlend` 0.18 s, smootherstep, overlapping the end of the old sign), from rest
  `fromRestBlend` 0.3 s, holds the last pose `holdBeforeRest` 0.7 s, then back to idle
  `toRestBlend` 0.5 s. Speeds up to `maxTempo` 1.35x when signs pile up in the queue.
  Fingerspelling plays library entries `FS_A`, `FS_B`, ... if all letters exist.
  It clears the Animator Controller at start (the graph drives the avatar).
- The library decides what has a clip; `has_animation` from Python is ignored in this mode.
- Optional message field `sign_id` (int, 0 = none) in `SignEntry`.
- Editor: **DGS > Sign Library Tools**
  1. Import from `SignBotController` (trigger -> clip) or from selected clips/FBX (next free id).
  2. Auto-trim: inPoint/outPoint where arm muscles leave / return to the idle pose.
  3. Smooth: Savitzky-Golay filter on all curves -> `Assets/SignClipsSmoothed/<GLOSS>.anim`
     (re-run any time; always starts from `sourceClip`).
  4. Export `sign_ids.json` (id, gloss) for the Python word -> number table.
- Adding a sign now: import the clip (Rig: Humanoid, Loop off, root "Bake Into Pose"),
  select it, "Add clips selected", fix the gloss, Auto-trim, Smooth, rebuild WebGL.

## Rocketbox avatar

- Microsoft Rocketbox (MIT, github.com/microsoft/Microsoft-Rocketbox), files in
  `Assets/Rocketbox/<Name>/Export/<Name>_facial.fbx` + `Assets/Rocketbox/<Name>/Textures/`.
  Use the `_facial.fbx` (has FACS `AU_..` and ARKit `AK_..` blendshapes), not the plain one.
- `Assets/Editor/RocketboxImport.cs` replaces the original `FixRocketboxMaxImport.cs`
  (never use both): only touches `/Rocketbox/` paths, Humanoid rig, external materials,
  blendshapes on, textures max 2048, hair Fade. Defaults only on first import.
- `Assets/Scripts/RocketboxFace.cs` on the avatar root: auto-blink, `Set(shape, 0..1)` /
  `Clear()` with smooth fading; context-menu test expressions (Play mode).
- `SignTcpClient` sits on the Rocketbox root (next to its Animator); the Mixamo `Ch23`
  object stays in the scene but disabled until the new avatar is confirmed.

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
