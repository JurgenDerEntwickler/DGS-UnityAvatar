# DGS-UnityAvatar – Unity client

Unity project for the DGS avatar. It receives DGS gloss sequences from the
Python backend (repo `JurgenDerEntwickler/DGSNEW`) and plays one sign animation
per gloss. On the Mac the project is `~/Desktop/DGS-UnityAvatar/Signbot`
(open it in Unity Hub via "Add project from disk").

## How it works

- `Assets/Scripts/SignTcpClient.cs` connects to `127.0.0.1:8765` on Start
  (the Python server `backend/live_sign_avatar.py` must already be running).
- It reads ONE JSON message per line, puts every sign into a queue and plays
  them one after another: fire the trigger, wait until the Animator leaves
  idle and returns to `Breathing Idle`, then the next sign.
- Signs with `has_animation: false` fire no trigger; they get a short pause
  (`missingSignPause`, 0.3 s) so the sentence rhythm stays.
- Inspector settings: `animator`, `host`, `port`, `idleStateName`,
  `missingSignPause`, `gapBetweenSigns`, `maxSecondsPerSign`.
- `UnityMainThreadDispatcher.cs` is no longer used by the client (kept, harmless).

## Message format (from Python)

```json
{"original_text": "...", "glosses": ["HALLO", "DANKE"],
 "signs": [{"gloss": "HALLO", "animation": "hallo", "has_animation": true,
            "fingerspell": false, "duration": 1.0}]}
```

Mapped by `SignMessage` / `SignEntry` in `SignTcpClient.cs`. If you change
fields, change `live_sign_avatar.py` in DGSNEW too.

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
