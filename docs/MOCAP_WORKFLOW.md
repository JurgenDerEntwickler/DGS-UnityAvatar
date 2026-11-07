# Adding DGS signs from video (MetaHuman markerless mocap)

This guide shows how to turn a video of a DGS sign into an animation for our MetaHuman
signer and add it to the live pipeline (speech -> translation -> Python -> Unreal).
One sign takes about 10-15 minutes of work plus processing time.

**Overview**

```
video (mp4)  ->  Capture Manager (import)  ->  MetaHuman Performance (Process)
             ->  Export Animation (MH_<GLOSS>)  ->  SignReceiver "Sign Clips" entry  ->  test
```

---

## 0. One-time setup

### 0.1 Software
- Unreal Engine **5.8** (Epic Games Launcher), with **MetaHuman Creator Core Data**.
- Visual Studio 2022 with *Game development with C++* (needed to build the project's C++ code).
- This repo (branch `metahuman-unreal`) plus the MetaHuman assets zip from Drive, unzipped into
  `Content/` (see `README.md`).

### 0.2 The markerless plugin (free, by Epic)
1. Epic Games Launcher -> **Fab** -> search **`markerless`** ->
   **MetaHuman Animator Markerless Motion Capture** -> *Add to My Library* -> *Install to Engine* -> 5.8.
   - If the download is corrupted: restart the Launcher (Windows Smart App Control / antivirus can interfere).
   - Do **not** confuse it with the *Depth Processing Plugin* (that one is for iPhone depth face capture).
2. Open the project -> **Edit -> Plugins** -> enable (and restart):
   - `MetaHuman Animator`
   - `MetaHuman Animator Markerless Motion Capture`
   - `Live Link Hub`
   - `Capture Manager Editor`
3. **Edit -> Project Settings** -> search `RHI` -> **Default RHI = DirectX 12** (required; restart after changing).

### 0.3 Hardware notes
Epic recommends 32 GB RAM, 16+ CPU cores, an 8 GB GPU (RTX 3070 class). It works with 16 GB RAM
but is slower: **close browser, Unity, chat apps etc. before processing.**

---

## 1. Record or find the video

**Content**
- **One sign per video.** Start and end with the hands relaxed at the sides.
- Correct DGS form. Best: record a DGS signer (Deaf student, interpreter, DGS teacher).
  Same person, same framing and light every time = consistent results.
- Online sources (e.g. SignDict) are fine for testing; check the licence and note the source
  (see the table in section 7).

**Camera / framing**
- At least **1080p**, 25 or 30 fps, camera on a tripod, no zoom/pan.
- Head to hips in frame, **both hands always fully visible**, also when raised above the head.
- Plain background, even light, no strong backlight. Dark, plain, long-sleeved top.
- Avoid motion blur: good light, and don't sign faster than normal.

**File naming and storage**
- Put each video in its own folder named after the gloss:
  `C:\DGS\SignVideos\<GLOSS>\<gloss>.mp4`, e.g. `C:\DGS\SignVideos\DANKE\danke.mp4`.
- **Do not commit videos to git** (size). Keep them in the shared Drive folder `SignVideos`.

---

## 2. Import the video (Capture Manager)

1. **Tools -> Live Link Hub** (separate window opens).
2. Top-left dropdown: **Capture Manager**.
3. First time only: **Data Devices -> Add -> Mono Video Ingest**, then in *Details*:
   - Display name: `SignVideos`
   - Source path: `C:\DGS\SignVideos` (subfolders are searched automatically)
   - Video Discovery Expression: leave `<Auto>`
4. Select your new take -> **Add to Queue** -> **Start**.
5. Result in the Content Drawer: `Content/CaptureManager/Imports/SignVideos/<gloss>/`
   with **`CD_<gloss>`** (Capture Data) and an Img Media Source.

If the ingest fails with an FPS error: Live Link Hub -> Settings -> *Use Third Party Encoder* -> path to `ffmpeg.exe`.

---

## 3. Process the motion (MetaHuman Performance)

1. Content Drawer -> `Content/Signs/Performance` -> right-click ->
   **MetaHuman -> MetaHuman Performance** -> name **`PF_<GLOSS>`** -> open -> Save.
2. **Details**:
   | Setting | Value |
   |---|---|
   | Input Type | Monocular Footage |
   | Footage Capture Data | `CD_<gloss>` |
   | Visualization Object | `SKM_MHC_Signer_BodyMesh` |
   | Facial Tracking | off (for now) |
   | **Body Tracking** | **on** |
   | Auto Body Height | on (or off + Body Height = 180 cm, our signer's height) |
   | Enable Foot-Locking | on |
3. **Trim:** scrub the timeline, note the frame where the hand starts to rise and where it is back
   down. Enter them (with ~3 frames margin) in **Start Frame to Process / End Frame to Process**.
4. Close other programs -> **Process** (toolbar) -> wait. A dark green bar marks processed frames.
5. Review in the *A|B Viewport* (zoom in): arm path, palm direction, fingers. Check that the
   **dominant hand** matches the video (videos can be mirrored).

**If Process is greyed out** (tooltip: *"Enable either face or body solving"*): the **Body Tracking**
checkbox is missing or off. Check DirectX 12 and that the markerless plugin is enabled, restart,
reopen the Performance, tick **Body Tracking**.

---

## 4. Export the animation

1. Toolbar **Export Animation** -> save to `Content/Signs`, name **`MH_<GLOSS>`**
   (e.g. `MH_DANKE`).
2. Export settings:
   | Setting | Value |
   |---|---|
   | Export Range | Processing Range |
   | Export Body | on |
   | **Export Skeleton** | **Existing Skeleton** (NOT *Performer Skeleton*) |
   | Target Skeleton or Skeletal Mesh | `SKM_MHC_Signer_BodyMesh` |
   | Body Retargeter | `RTG_MH_IKRig` (filled in automatically) |
   | Curve Interpolation | **Cubic** (smoother than Linear) |
   | Remove Redundant Curve Keys | on |
3. **Create** -> **Save All** (Ctrl+Shift+S).
4. Double-click `MH_<GLOSS>` to preview it.

---

## 5. Add the sign to the pipeline

1. Open `Content/Maps/SignerLevel`.
2. Outliner -> search `receiver` -> select **SignReceiver**.
3. Details -> search `sign clips` -> **+** -> new entry:
   - **Key:** the gloss in CAPITALS, exactly as Python sends it, umlauts written out
     (`DANKE`, `KOENNEN`, `VERSTEHEN`).
   - **Anim:** `MH_<GLOSS>`
   - **In Point / Out Point** (0..1): skip the rest pose at start and end, so signs flow into each
     other and the hands never pass through the body:
     - open `MH_<GLOSS>`, note the frame where the hand has clearly **left** the body and the frame
       where it **arrives back**, and the total number of frames;
     - `In Point = start frame / total`, `Out Point = end frame / total`
       (example: 10 / 48 = 0.21 and 40 / 48 = 0.83).
   - **Play Rate:** 1 (raise slightly if the sign is too slow).
4. Ctrl+S.

**Hands inside the body at the start/end?** The person in the video is slimmer than our MetaHuman.
Fix it with In/Out Point (above); the receiver then blends from his own idle pose instead.

---

## 6. Test

1. Start the backend (in `DGSNEW`):
   ```powershell
   cd C:\DGS\DGSNEW
   .venv\Scripts\Activate.ps1
   python backend\web_app.py --unity-editor --keep-running
   ```
2. Unreal: **Play** in SignerLevel. Output Log, filter `LogSign`:
   `Connected to Python server 127.0.0.1:8765`
3. Type a sentence with the word on the web page (or use the microphone). Log shows
   `Received ... -> ... DANKE ...` and `> DANKE`, and the MetaHuman signs it.
   `- DANKE (no clip, ...)` means the key in Sign Clips does not match the gloss.

---

## 7. Commit and document

1. Add a line to the sign table below.
2. Commit only the Unreal assets, not the videos:
   ```powershell
   cd C:\DGS\DGSMetaHuman
   git add -A
   git commit -m "Add sign DANKE (mocap)"
   git push team HEAD:metahuman-unreal
   ```

**Avoid merge conflicts:** `SignerLevel.umap` is a binary file. If two people add Sign Clips entries at
the same time, git cannot merge them. Agree who edits `SignerLevel` (or add your `MH_<GLOSS>` and ask
that person to add the Sign Clips entry). Pull before you start: `git pull team metahuman-unreal`.

### Sign table

| Gloss | Animation | In / Out | Video source | Licence | Added by |
|---|---|---|---|---|---|
| HALLO | `MH_HALLO_Mocap` | tbd | gebaerdenlernen.de (H. M. Bartsch) via SignDict | CC BY-NC-SA 3.0 DE | Genti |

---

## Naming conventions

| What | Name / place |
|---|---|
| Videos (not in git, Drive) | `SignVideos/<GLOSS>/<gloss>.mp4` |
| Capture data | `Content/CaptureManager/Imports/SignVideos/<gloss>/CD_<gloss>` |
| Performance | `Content/Signs/Performance/PF_<GLOSS>` |
| Animation (played by the receiver) | `Content/Signs/MH_<GLOSS>` |
| Fingerspelling letters (later) | `MH_FS_A` ... with Sign Clips keys `FS_A` ... |
| Sign Clips key | gloss in capitals, umlauts written out: `KOENNEN` |

## Known limitations
- The markerless plugin is **experimental** in UE 5.8; finger detail depends on video resolution.
  Fix wrong handshapes later in Sequencer (bake to Control Rig, correct fingers, bake back).
- Facial expressions (DGS grammar) are not captured yet - planned as a separate step.
