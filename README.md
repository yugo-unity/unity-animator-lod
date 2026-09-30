# OptimizedAnimators

English | [日本語](README.ja.md)

A test project for reducing CPU/GPU cost when many Animators are on screen. The core is the `AnimatorLod` component and `AnimatorLodSystem`, which processes all instances in one place.

## Layout

| Kind | File | Role |
|---|---|---|
| Runtime | `Assets/Scripts/AnimatorLod.cs` | Per-instance settings and state, one per GameObject with an Animator. Requires `Animator` |
| Runtime | `Assets/Scripts/AnimatorLodSystem.cs` | Static class that evaluates LOD and toggles Animators for all registered instances. Appended to the end of the PlayerLoop Update phase (after user scripts' Update, coroutines, async continuations, and DirectorUpdate), so state changes and `RequestImmediateUpdate()` calls made there take effect before the Animator is evaluated in the same frame. Its state is rebuilt at the start and end of every Play session, so it also works with Enter Play Mode Options that disable domain reload |
| Editor | `Assets/Scripts/Editor/AnimatorLodEditor.cs` | Inspector and Scene View LOD label |
| Editor | `Assets/Scripts/Editor/AnimatorLodBoundsCalculator.cs` | Measures the Bounds used for LOD from all clips or the default pose |
| Editor | `Assets/Scripts/Editor/AnimatorLodMeshReducer.cs` | Generates reduced meshes for Mesh LOD |
| Benchmark | `Assets/Benchmark/` | Spawner, HUD, and control panel for the test scene (AnimatorLodTest) |

## Inspector

### References

Read-only. Both fields are fetched automatically when the component is attached.

| Name | Description |
|---|---|
| Animator | The Animator on the same GameObject. `Animator` is a required component. |
| Skinned Mesh Renderers | All SkinnedMeshRenderers in the children, including inactive ones. |
| Refresh References | Fetches both fields again. Use it after changing the hierarchy. |

Along with the references, each renderer's own settings (mesh and skin weights) are prepared in the Editor, so Awake does no lookups or conversions. They are recaptured automatically when the AnimatorLod is edited in the Inspector and on Refresh References. **If you change a renderer's mesh or skin weights afterwards without touching the AnimatorLod, press Refresh References.** Otherwise the old values stay, and they are what the renderer is restored to when a feature is turned off at runtime.

A component that was not prepared in the Editor (for example, one added with `AddComponent` at runtime) is not supported. It logs an error in Awake and disables itself. Add the component in the Editor and put it on a prefab.

Renderers whose reference is missing (for example, deleted from the hierarchy after Refresh References) are dropped at Awake with a warning. A renderer may be destroyed on its own (for example, when swapping equipment); the next time the renderers are used, it is dropped with a warning. The Animator is expected to be destroyed only together with the AnimatorLod. Destroying the Animator on its own while the AnimatorLod is active is not supported; runtime code does not check it for null.

### LOD

LOD boundaries and the LOD Camera are shared by Animation LOD, Mesh LOD, and Skin Weights. LOD is evaluated while any of the three is enabled, once per evaluation for all three (never separately per feature).

LOD is not evaluated every frame:

- While Animation LOD is enabled, an instance evaluates LOD only on the frames its Animator is evaluated. It is also evaluated right away when its visibility changes, after `RequestImmediateUpdate()`, after a setting changes (the `Set*Enabled` methods or an Inspector edit), and on the first tick after the component is enabled. Visibility itself is still checked every frame, so an instance that comes on screen is woken up immediately.
- While Animation LOD is disabled (Mesh LOD / Skin Weights only), LOD is evaluated once every LOD Evaluation Interval frames, spread across instances.

As a result, an LOD change (including the Mesh LOD / Skin Weights switch) can lag by up to the instance's update interval, or by up to the LOD Evaluation Interval while Animation LOD is disabled.

| Name | Description |
|---|---|
| Bounds | AABB in this transform's local space. Used as the bounding sphere for LOD. It is calculated in the same way as Calculate from All Clips when the component is attached. While selected, the Scene View shows the AABB (yellow) and the bounding sphere (cyan). |
| Status | Where Bounds came from: Calculated from all clips / Calculated from default pose / Taken from renderer bounds (for example, attached without a Controller). |
| Calculate from All Clips | Samples every clip and pose of the Animator Controller to measure Bounds so it contains every pose. Airborne clips and similar can extend it below the feet or make it asymmetric. If the Animator has no Controller, the renderers' current bounds (current pose) are used instead, without an error. Nothing is calculated at runtime. |
| Calculate from Default Pose | Measures Bounds from the default pose with no animation applied (the bone pose saved in the prefab or scene instance). Works with or without a Controller and with Optimize Game Objects. Depending on the pose, the mesh may extend outside Bounds. Nothing is calculated at runtime. |
| LOD Camera | Camera used for the screen-size ratio. Empty = Camera.main. If neither exists, the ratio is treated as 100% (LOD 0). Only one camera is used (split screens and other multi-camera views are not taken into account). |
| LOD Evaluation Interval | Evaluation interval (frames, 1–8) while Animation LOD is disabled (see above). 1 = every frame. Grayed out while Animation LOD is enabled, or while Mesh LOD and Skin Weights are both disabled. |
| Ratios | LOD is chosen by the screen-size ratio, the fraction of the screen height covered by the bounding sphere of Bounds. Left is 100% (near), right is 0% (far). Drag the boundaries to edit. |
| LOD n Transition (% Screen Size) | Enter LOD n when the ratio falls below this value. Boundaries cannot cross their neighbours (descending order). |
| Culled (% Screen Size) | Below this value the instance is treated as LOD-culled and the Invisible Interval applies. 0 = off. Mesh LOD and Skin Weights use the last LOD's settings. |
| - Level / + Level | Removes or adds an LOD level. |

The screen-size ratio is an approximation: the sphere radius is the radius of the bounding sphere of Bounds multiplied by the largest per-axis world scale (the column lengths of `localToWorldMatrix`, which equal the absolute `lossyScale` components when there is no shear). Perspective and orthographic cameras are both supported: with a perspective camera the ratio is this radius ÷ (distance to the camera × tan(FOV / 2)), and with an orthographic camera it is this radius ÷ `orthographicSize`, independent of distance.

#### Animation LOD

| Name | Description |
|---|---|
| Enabled | Throttles the Animator update per LOD. Mesh LOD and Skin Weights keep working while this is off (the Animator is then evaluated every frame). |
| Base Interval | Animator update interval (frames, 0–4) added to every LOD. LOD 0 runs at this value alone. 0/1 = every frame. |
| Invisible Interval | Interval (frames, 0–4) used when no renderer is visible to any camera, or when culled. Overrides LOD and Base. Visibility uses the same test as the Animator's culling (Renderer.isVisible), but only the renderers in References are checked, whereas the Animator checks every renderer under it. |
| LOD n Interval | Interval (frames, 0–4) added to Base Interval for LOD n. The effective interval is Base + this value (up to 8). |

With interval N, the Animator is enabled once every N frames. On each frame it runs, `speed` is set to `Speed` × the time elapsed since its previous evaluation ÷ this frame's deltaTime, which compensates for the skipped time. The elapsed time is measured on the scaled or unscaled clock to match the Animator's Update Mode, so the Animator advances by the sum of the skipped frames' deltaTime even when the frame rate varies (at a constant frame rate this equals the number of elapsed frames). It also stays correct right after the interval changes (LOD, visibility, or a runtime change of Base / Invisible Interval) and after `RequestImmediateUpdate()`. Instances with the same interval are spread across phase buckets so they do not pile up on one frame. On the first tick after the component is enabled, the instance is evaluated once regardless of its interval, so a newly spawned instance is never drawn in the prefab's pose.

On skipped frames the Animator itself does not run, so note the following (confirmed in the Editor):

- Parameters and triggers set from script take effect on the next evaluated frame. Call `RequestImmediateUpdate()` to apply them at once.
- Animation events, `OnAnimatorMove`, `OnAnimatorIK`, root motion, and objects attached to bones are processed only on evaluated frames, and the skipped time is applied in one step. Animation events are not lost, but they can fire up to (interval − 1) frames late.

Animators whose Update Mode is Fixed (Animate Physics) are not supported. They are evaluated in FixedUpdate, so the per-frame throttling and `speed` compensation do not match. A warning is logged on registration (once per Play session). Use Normal or Unscaled Time.

#### Mesh LOD

| Name | Description |
|---|---|
| Enabled | Swaps SkinnedMeshRenderer.sharedMesh to a reduced mesh per LOD to cut the number of skinned vertices. |
| LOD n / Element i | Mesh used for the i-th renderer in References at LOD n. Empty = original mesh. LOD 0 always uses the original mesh. |
| Generate Reduced Meshes | Simplifies with Unity's built-in Mesh LOD generator (`MeshLodUtility.GenerateMeshLods`, Editor only), extracts only the vertices each level uses into standalone meshes under `Assets/Generated/MeshLod/`, and assigns them to the fields above. |

Notes:

- Replacement meshes must share the original skeleton (same bindpose order and count). The generator copies bone weights and bindposes from the original mesh.
- On rigs with Optimize Game Objects, the renderer binds to the Animator skeleton through the mesh's bone name hashes. The generator copies these hashes from the original mesh as well.
- The generated meshes are saved with Read/Write disabled, so no copy stays in main memory. While a LOD mesh is applied, reading the vertices or other data of `SkinnedMeshRenderer.sharedMesh` from a script at runtime logs an error and returns an empty array (no exception; confirmed in Editor Play mode and in a player). `sharedMesh` also returns the LOD mesh during that time.
- Unity's standard Mesh LOD (the Generate Mesh LODs import option) shares one vertex buffer and only switches index ranges. It reduces drawn triangles but not skinned vertices, which is why the generator writes standalone meshes.
- Visual quality of the generated meshes is not considered (for testing).
- Regenerating overwrites existing assets in place (GUIDs and references are kept).
- Where the gain shows up depends on the skinning mode: CPU time with CPU skinning, GPU time with GPU skinning.

#### Skin Weights

| Name | Description |
|---|---|
| Enabled | Switches SkinnedMeshRenderer.quality (bones per vertex) per LOD. |
| LOD n | Skin weights for LOD n. Auto = the renderer's own setting. LOD 0 always uses the renderer's own setting. |

Notes:

- `QualitySettings.skinWeights` is the upper limit. Higher bone counts have no effect.

#### Bone-weight buffer warm-up (Mesh LOD and Skin Weights)

GPU skinning creates a bone-weight GPU buffer per mesh and bone count the first time that pair is drawn, and the creation cost lands on that frame.

- AnimatorLod creates these buffers in advance only for the pairs that LOD 1 and above use with the current Mesh LOD / Skin Weights settings. The LOD 0 pair is left to normal rendering. Each pair is created once.
- The buffers are created when the component is enabled, when Mesh LOD or Skin Weights is turned on or off at runtime, and when values are changed in the Inspector during Play mode (Editor only).
- To create them before any instance is spawned (for example, during loading), call `Prewarm()`. It also works on a prefab asset, without instantiating it or modifying the asset.
- If `QualitySettings.skinWeights` is changed at runtime, buffers for newly needed bone counts are not created in advance; they are created the first time they are drawn (rendering is still correct). Call `Prewarm()` again after the change to create them in advance.
- The record of created buffers holds meshes through weak references, so it does not keep meshes from being unloaded by `Resources.UnloadUnusedAssets` or by releasing an AssetBundle. A mesh that is loaded again after being unloaded gets its buffers created again. Records of unloaded or destroyed meshes are removed the next time a new mesh is recorded.

## Scene View

Selecting an AnimatorLod in the Hierarchy shows its current LOD above it.

- In Play mode: the AnimatorLodSystem result (LOD, screen-size ratio, interval, phase bucket, visibility, and current Mesh LOD / Skin Weights state).
- In Edit mode: a preview using the same formula as runtime with the LOD Camera (or Camera.main, or the Scene camera if neither exists).

## Runtime API

| API | Description |
|---|---|
| `SetLodEnabled(bool)` | Enable or disable Animation LOD |
| `SetMeshLodEnabled(bool)` | Enable or disable Mesh LOD. Creates the bone-weight buffers the new setting needs, and restores the original mesh when disabled |
| `SetSkinWeightsLodEnabled(bool)` | Enable or disable Skin Weights. Creates the bone-weight buffers the new setting needs, and restores the original setting when disabled |
| `Prewarm()` | Creates the bone-weight buffers that Mesh LOD / Skin Weights use right now. Also works on a prefab asset |
| `BaseInterval` / `InvisibleInterval` | Get or set the update intervals. Values outside 0–4 are clamped |
| `LodEvaluationInterval` | Get or set the LOD Evaluation Interval. Values outside 1–8 are clamped |
| `Speed` | Animator playback speed. Use this instead of `Animator.speed` (see below) |
| `RequestImmediateUpdate()` | Call right after changing Animator state or parameters from outside. The instance is evaluated once on the next tick even while throttled. Requests made while the component or Animation LOD is disabled are discarded (the instance is evaluated anyway right after it is enabled, and every frame while Animation LOD is disabled) |
| `AnimatorLodSystem.Instances` | All registered instances. A new list is used for each Play session, so do not keep it across sessions |
| `AnimatorLodSystem.InvisibleCount` / `EnabledThisFrame` | Number of invisible instances, and of Animators enabled, in the latest tick |

Read-only state (per instance):

| API | Description |
|---|---|
| `LodEnabled` / `MeshLodEnabled` / `SkinWeightsLodEnabled` | Whether Animation LOD / Mesh LOD / Skin Weights is enabled |
| `LodCamera` / `LodBounds` / `LodBoundsSource` | LOD Camera, Bounds, and where Bounds came from (the Inspector's Status) |
| `LodLevelCount` | Number of LOD levels (boundaries + 1) |
| `GetEffectiveInterval(level)` | Effective interval of LOD `level` (Base Interval + LOD n Interval; Invisible Interval not considered) |
| `CurrentLod` | LOD from the latest evaluation. -1 before the first evaluation and while the component or all three features are disabled; `LodLevelCount` while culled |
| `CurrentScreenRatio` / `IsCulled` | Screen-size ratio and culled state from the latest evaluation |
| `IsInvisible` / `CurrentInterval` | Visibility and applied update interval from the latest tick. Always `false` / 0 while Animation LOD is disabled |
| `CurrentBucket` | Assigned phase bucket (0 while the Animator is evaluated every frame) |

`SetMeshLodEnabled` and `SetSkinWeightsLodEnabled` change the renderers immediately only while the component is enabled. While it is disabled, only the setting is stored, and it takes effect after the component is enabled again.

While an instance is registered, `Animator.enabled` and `Animator.speed` are managed by `AnimatorLodSystem`. Do not change them from outside.

- **Speed**: Set `AnimatorLod.Speed` instead of `Animator.speed` (for slow motion, pausing with 0, and so on). It takes effect on the next evaluated frame, with the skipped time compensated. When the component is enabled, the current `Animator.speed` becomes the initial `Speed`; when it is disabled, `Animator.speed` is set back to `Speed`. While the component is disabled, you may change `Animator.speed` directly.
- **Stopping the Animator**: Disable the AnimatorLod first, then the Animator. When the AnimatorLod is disabled, it re-enables the Animator.

Values changed in the Inspector during Play mode (LOD Camera, Bounds, per-LOD meshes and skin weights, and so on) take effect on the next tick (Editor only).

For a newly added component, Mesh LOD and Skin Weights are disabled by default.

## Constraints

A summary of the notes in the sections above.

- `Animator.speed` and `Animator.enabled` cannot be changed directly (use `Speed`; to stop the Animator, disable the AnimatorLod first).
- Awake sets `Animator.keepAnimatorStateOnDisable` to `true`. The setting stays after the AnimatorLod is removed.
- Mesh LOD and Skin Weights overwrite `SkinnedMeshRenderer.sharedMesh` and `quality`. Swapping the mesh of the same renderer at runtime (for example, a costume change) conflicts with them. Turning a feature off restores the values recorded in the Editor.
- On skipped frames the Animator does not run. Parameter changes, animation events, `OnAnimatorMove`, IK, root motion, and objects attached to bones advance together on the evaluated frame (see Animation LOD).
- LOD uses an approximation based on the bounding sphere of Bounds and the largest per-axis world scale, and only one camera.
- Replacement meshes must share the original skeleton (same bindpose order and count).
- Generated LOD meshes have Read/Write disabled. Reading their vertices or other data from a script at runtime while they are applied returns an empty array.
- A component that was not prepared in the Editor (for example, added with `AddComponent` at runtime) is not supported.
- Animators whose Update Mode is Fixed (Animate Physics) are not supported.
- `AnimatorLodSystem` is appended to the end of the PlayerLoop Update phase once. If another system later calls `PlayerLoop.SetPlayerLoop` with a rebuilt default tree, it is removed and does not run until the next Play session (in a build, the next launch).

## Optimization ideas (not implemented)

### Shared LOD settings asset (ScriptableObject profile)

Move the LOD settings that every instance of a prefab shares (Ratios, LOD n Transition / Interval, Culled, Base / Invisible Interval, Skin Weights per LOD) out of `AnimatorLod` into a ScriptableObject profile, and have each `AnimatorLod` reference a profile.

- **Why**: Today every instance deserializes its own copy of these settings when it is instantiated, including three arrays (`lodRatios`, `lodIntervals`, `lodSkinQualities`). With a shared profile, those arrays exist once per profile instead of once per instance, which cuts GC allocations and memory at spawn time. For reference, instantiating `Armature_Lod` currently costs 17 GC allocations per instance more than the same object without `AnimatorLod` (measured in the Editor). The saving from the profile is estimated at about three of those, one per array; this has not been measured.
- **Side benefit**: Editing a profile updates every character that uses it at once.
- **Why it is not implemented**: The workflow becomes more complex.
  - An extra asset has to be created and assigned per character type.
  - Inspector edits change a shared asset and affect every user of it.
  - Per-instance tweaks need either a separate profile or an override mechanism.
  - `BaseInterval` / `InvisibleInterval` are per-instance runtime setters today, so they would have to stay on the component or change meaning.
- **Out of scope**: Mesh LOD meshes and the per-renderer data stay on the component, because they are tied to the instance's own renderer references.
