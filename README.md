# OptimizedAnimators

English | [日本語](README.ja.md)

A test project for reducing CPU/GPU cost when many Animators are on screen. The core is the `AnimatorLod` component and `AnimatorLodSystem`, which processes all instances in one place.

## Layout

| Kind | File | Role |
|---|---|---|
| Runtime | `Assets/Scripts/AnimatorLod.cs` | Per-instance settings and state, one per GameObject with an Animator. Requires `Animator` |
| Runtime | `Assets/Scripts/AnimatorLodSystem.cs` | Static class that evaluates LOD and toggles Animators for all registered instances. Appended to the end of the PlayerLoop Update phase (after user scripts' Update, coroutines, async continuations, and DirectorUpdate), so state changes and `RequestImmediateUpdate()` calls made there take effect before the Animator is evaluated in the same frame. Its state is rebuilt at the start and end of every Play session, so it also works with Enter Play Mode Options that disable domain reload |
| Editor | `Assets/Scripts/Editor/AnimatorLodEditor.cs` | Inspector and Scene View LOD label |
| Editor | `Assets/Scripts/Editor/AnimatorLodBoundsCalculator.cs` | Measures the Static Bounds AABB from all clips |
| Editor | `Assets/Scripts/Editor/AnimatorLodMeshReducer.cs` | Generates reduced meshes for Mesh LOD |
| Benchmark | `Assets/Benchmark/` | Spawner, HUD, and control panel for the test scenes (AnimatorLodTest / AnimatorStressTest). `Test/` holds a device measurement script (runs only when a Development player is launched with the `-alodStaticBoundsBench` argument) |

## Inspector

### References

Read-only. Both fields are fetched automatically when the component is attached.

| Name | Description |
|---|---|
| Animator | The Animator on the same GameObject. `Animator` is a required component. |
| Skinned Mesh Renderers | All SkinnedMeshRenderers in the children, including inactive ones. |
| Refresh References | Fetches both fields again. Use it after changing the hierarchy. |

If a field is still empty at runtime, it is fetched at Awake as before, and a warning is logged because the lookup then runs at runtime. Renderers whose reference is missing (for example, deleted from the hierarchy after Refresh References) are dropped at Awake with a warning.

A renderer may be destroyed on its own (for example, when swapping equipment). The next time the renderers are used, it is dropped with a warning. The Animator is expected to be destroyed only together with the AnimatorLod. Destroying the Animator on its own while the AnimatorLod is active is not supported; runtime code does not check it for null.

Along with each renderer, its own settings (mesh, rootBone, localBounds, skin weights) and the fixed bounds for Static Bounds are prepared in the Editor, so Awake does no lookups or conversions. These are recaptured automatically when the AnimatorLod is edited in the Inspector, when Calculate Bounding runs, and on Refresh References. **If you change a renderer's mesh, rootBone, or skin weights afterwards without touching the AnimatorLod, press Refresh References.** Otherwise the old values stay, and they are what the renderer is restored to when a feature is turned off at runtime.

### LOD

LOD boundaries are shared by Animation LOD, Mesh LOD, and Skin Weights.

| Name | Description |
|---|---|
| Bounds | AABB in this transform's local space. Always used as the bounding sphere for LOD. It is calculated in the same way as Calculate Bounding when the component is attached. |
| Calculate Bounding | Samples every clip and pose of the Animator Controller to measure Bounds so it contains every pose. If the Animator has no Controller, the renderers' current bounds (current pose) are used instead, without an error. Nothing is calculated at runtime. |
| Static Bounds | Removes the renderer's rootBone and assigns Bounds as a fixed localBounds. It only controls this feature; Bounds is used for LOD either way. Off by default (see the notes below). |
| Ratios | LOD is chosen by the screen-size ratio, the fraction of the screen height covered by the bounding sphere of Bounds. Left is 100% (near), right is 0% (far). Drag the boundaries to edit. |
| LOD n Transition (% Screen Size) | Enter LOD n when the ratio falls below this value. Boundaries cannot cross their neighbours (descending order). |
| Culled (% Screen Size) | Below this value the instance is treated as LOD-culled and the Invisible Interval applies. 0 = off. Mesh LOD and Skin Weights use the last LOD's settings. |
| - Level / + Level | Removes or adds an LOD level. |

Static Bounds notes:

- The fixed bounds contain every pose of every clip, so they are larger than the default bounds that follow the rootBone (the import-time bounds). Instances just off screen are then culled less often, and more of them are drawn and skinned. In AnimatorLodTest (default settings, 200 instances), the drawn skinned meshes went from 160 to 170, and draw calls and triangles rose by about 6-8% (measured in the Editor).
- When the SkinnedMeshRenderer's Update When Offscreen is off (the default), bounds are not recalculated from the bones every frame even without Static Bounds (confirmed by `Mesh.CalcBoneBounds` not appearing in the Profiler). Little work is saved, and the extra rendering cost above outweighs it.
- A benefit is expected only when Update When Offscreen is on and bounds are computed from the bones every frame (not measured).

#### Animation LOD

| Name | Description |
|---|---|
| Enabled | Evaluates LOD every frame. **Mesh LOD and Skin Weights only work while this is enabled.** |
| LOD Camera | Camera used for the screen-size ratio. Empty = Camera.main. |
| Base Interval | Animator update interval (frames) added to every LOD. LOD 0 runs at this value alone. 0/1 = every frame. |
| Invisible Interval | Interval used when no renderer is visible to any camera, or when culled. Overrides LOD and Base. Visibility uses the same test as the Animator's culling (Renderer.isVisible), but only the renderers in References are checked, whereas the Animator checks every renderer under it. |
| LOD n Interval | Interval added to Base Interval for LOD n. The effective interval is Base + this value. |

With interval N, the Animator is enabled once every N frames. On each frame it runs, `speed` is set to `Speed` × the number of frames since its previous evaluation, which compensates for the skipped time. The frame count is N in the steady state, and it also stays correct right after the interval changes (LOD, visibility, or a runtime change of Base / Invisible Interval) and after `RequestImmediateUpdate()`. Instances with the same interval are spread across phase buckets so they do not pile up on one frame. On the first tick after the component is enabled, the instance is evaluated once regardless of its interval, so a newly spawned instance is never drawn in the prefab's pose.

Animators whose Update Mode is Fixed (Animate Physics) are not supported. They are evaluated in FixedUpdate, so the frame-based throttling and `speed` compensation drift. A warning is logged on registration (once per Play session). Use Normal or Unscaled Time.

#### Mesh LOD

| Name | Description |
|---|---|
| Enabled | Swaps SkinnedMeshRenderer.sharedMesh to a reduced mesh per LOD to cut the number of skinned vertices. |
| LOD n / Element i | Mesh used for the i-th renderer in References at LOD n. Empty = original mesh. LOD 0 always uses the original mesh. |
| Generate Reduced Meshes | Simplifies with Unity's built-in Mesh LOD generator (`MeshLodUtility.GenerateMeshLods`, Editor only), extracts only the vertices each level uses into standalone meshes under `Assets/Generated/MeshLod/`, and assigns them to the fields above. |

Notes:

- Replacement meshes must share the original skeleton (same bindpose order and count). The generator copies bone weights and bindposes from the original mesh.
- On rigs with Optimize Game Objects, the renderer binds to the Animator skeleton through the mesh's bone name hashes. The generator copies these hashes from the original mesh as well.
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
- The engine builds a bone-weight cache per mesh and per bone count on first use. When enabled, the caches for the mesh and bone-count combinations in use are built in advance when the component is enabled, or when Skin Weights is turned on at runtime (once per mesh).
- Which meshes have been cached is recorded by holding references to the meshes, and the record is kept until the Play session ends (in a build, until the application quits). If runtime-generated meshes are used for Mesh LOD and repeatedly destroyed and recreated, one record per destroyed mesh stays behind (each is small, so this is not a concern for normal use).

## Scene View

Selecting an AnimatorLod in the Hierarchy shows its current LOD above it.

- In Play mode: the AnimatorLodSystem result (LOD, screen-size ratio, interval, phase bucket, visibility, and current Mesh LOD / Skin Weights state).
- In Edit mode: a preview using the same formula as runtime with the LOD Camera (or Camera.main, or the Scene camera if neither exists).

## Runtime API

| API | Description |
|---|---|
| `SetStaticBounds(bool)` | Enable or disable Static Bounds |
| `SetLodEnabled(bool)` | Enable or disable Animation LOD |
| `SetMeshLodEnabled(bool)` | Enable or disable Mesh LOD. Restores the original mesh when disabled |
| `SetSkinWeightsLodEnabled(bool)` | Enable or disable Skin Weights. Builds caches when enabled and restores the original setting when disabled |
| `BaseInterval` / `InvisibleInterval` | Get or set the update intervals |
| `Speed` | Animator playback speed. Use this instead of `Animator.speed` (see below) |
| `RequestImmediateUpdate()` | Call right after changing Animator state or parameters from outside. The instance is evaluated once on the next tick even while throttled. Requests made while the component or Animation LOD is disabled are discarded (the instance is evaluated anyway right after it is enabled, and every frame while LOD is disabled) |
| `AnimatorLodSystem.Instances` | All registered instances. A new list is used for each Play session, so do not keep it across sessions |
| `AnimatorLodSystem.InvisibleCount` / `EnabledThisFrame` | Number of invisible instances, and of Animators enabled, in the latest tick |

`SetStaticBounds`, `SetMeshLodEnabled`, and `SetSkinWeightsLodEnabled` change the renderers immediately only while the component is enabled. While it is disabled, only the setting is stored, and it takes effect after the component is enabled again.

While an instance is registered, `Animator.enabled` and `Animator.speed` are managed by `AnimatorLodSystem`. Do not change them from outside.

- **Speed**: Set `AnimatorLod.Speed` instead of `Animator.speed` (for slow motion, pausing with 0, and so on). It takes effect on the next evaluated frame as `Speed` × elapsed frames. When the component is enabled, the current `Animator.speed` becomes the initial `Speed`; when it is disabled, `Animator.speed` is set back to `Speed`. While the component is disabled, you may change `Animator.speed` directly.
- **Stopping the Animator**: Disable the AnimatorLod first, then the Animator. When the AnimatorLod is disabled, it re-enables the Animator.

Values changed in the Inspector during Play mode (LOD Camera, Static Bounds, Bounds, per-LOD meshes and skin weights, and so on) take effect on the next tick (Editor only).

For a newly added component, Static Bounds, Mesh LOD, and Skin Weights are disabled by default.

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
