using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace AnimatorStressTest
{
    /// <summary>
    /// 登録された全 <see cref="AnimatorLod"/> の LOD 判定・可視判定・更新間隔の決定と Animator の切替を 1 か所で行う静的クラス。
    /// PlayerLoop の Update 段の末尾で毎フレーム 1 回 Tick する。
    /// </summary>
    public static class AnimatorLodSystem
    {
        /// <summary>有効な更新間隔の最大(Base + LOD 加算)。</summary>
        public const int MaxEffectiveInterval = AnimatorLod.MaxInterval * 2;

        /// <summary>speed で補償する経過フレーム数の上限(Interval の変更でバケットが替わったときの最大の空き)。</summary>
        private const int MaxElapsedFrames = MaxEffectiveInterval * 2;

        /// <summary>個体ごとのスケジュール状態(読み書きは AnimatorLodSystem だけが行う)。</summary>
        internal struct InstanceSchedule
        {
            /// <summary>登録リスト内の位置。未登録は -1。</summary>
            public int Index;
            /// <summary>割り当て中のバケットの Interval。1 以下は未割り当て。</summary>
            public int Interval;
            public int Bucket;
            public int LastEvaluatedFrame;
            /// <summary>登録後まだ評価していない(初回はバケットに関わらず評価する)。</summary>
            public bool FirstEvaluationPending;
            /// <summary>最後に設定した Animator.speed。NaN は未設定。</summary>
            public float AppliedSpeed;
            /// <summary>最後に設定した Animator.enabled。</summary>
            public bool AppliedEnabled;

            public static InstanceSchedule Unregistered => new InstanceSchedule { Index = -1 };
        }

        /// <summary>Play 単位の状態(作り直すとリセットされる)。</summary>
        private sealed class State
        {
            public readonly List<AnimatorLod> Instances = new List<AnimatorLod>(1024);
            public readonly int[][] BucketCounts = CreateBucketCounts();
            /// <summary>Mesh ごとに温めた Skin Weights のビットマスク(1 &lt;&lt; SkinQuality)。</summary>
            public readonly Dictionary<Mesh, int> WarmedSkinWeights = new Dictionary<Mesh, int>();
            public int LastTickFrame = -1;
            // Transform のマネージドラッパーを強参照で保持し、cam.transform 経由の散発的な GC.Alloc(ラッパー再生成)を避ける
            public Camera MainCamera;
            public Transform MainCameraTransform;
            public int InvisibleCount;
            public int EnabledThisFrame;
            /// <summary>Update Mode が Fixed の警告を出したか(Play ごとに 1 回)。</summary>
            public bool FixedUpdateModeWarned;
        }

        /// <summary>Tick 1 回分の値。</summary>
        private struct TickContext
        {
            public int Frame;
            public AnimatorLod.LodView MainView;
            // 直前の個体の LodCamera とその LodView(同じカメラが続く間は計算を使い回す)
            public Camera LodCamera;
            public AnimatorLod.LodView LodCameraView;
        }

        private static State s_state = new State();
        // 以下は Play 単位ではなく、PlayerLoop / Editor に残るオブジェクトの状態を映す
        private static bool s_loopInstalled;
        private static Mesh s_bakeScratch;

        /// <summary>登録中の全個体。Play をまたいで保持しないこと。</summary>
        public static IReadOnlyList<AnimatorLod> Instances => s_state.Instances;

        /// <summary>直近の Tick で不可視だった個体数。</summary>
        public static int InvisibleCount => s_state.InvisibleCount;

        /// <summary>直近の Tick で Animator を有効化した個体数。</summary>
        public static int EnabledThisFrame => s_state.EnabledThisFrame;

        private static int[][] CreateBucketCounts()
        {
            var counts = new int[MaxEffectiveInterval + 1][];
            for (int n = 0; n <= MaxEffectiveInterval; n++)
            {
                counts[n] = new int[Mathf.Max(1, n)];
            }
            return counts;
        }

        // ---- ライフサイクル ----

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            // Enter Play Mode でドメインリロードが無効でも、前回 Play の残骸を持ち越さない
            ResetState();
            InstallPlayerLoop();
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void HookEditorPlayMode()
        {
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
            {
                RemovePlayerLoop();
                ResetState();
                // HideAndDontSave はドメインリロードを越えて残るが static の参照は失われるため、Play ごとに破棄する
                if (s_bakeScratch != null)
                {
                    Object.DestroyImmediate(s_bakeScratch);
                    s_bakeScratch = null;
                }
            }
        }
#endif

        /// <summary>登録中の個体を未登録に戻してから、Play 単位の状態を作り直す。</summary>
        private static void ResetState()
        {
            var instances = s_state.Instances;
            for (int i = 0; i < instances.Count; i++)
            {
                instances[i].Schedule.Index = -1;
            }
            s_state = new State();
        }

        private static void InstallPlayerLoop()
        {
            if (s_loopInstalled)
            {
                return;
            }

            var root = PlayerLoop.GetCurrentPlayerLoop();
            if (AppendToPhase(ref root, typeof(Update),
                new PlayerLoopSystem { type = typeof(AnimatorLodSystem), updateDelegate = Tick }))
            {
                PlayerLoop.SetPlayerLoop(root);
                s_loopInstalled = true;
            }
            else
            {
                Debug.LogError("[AnimatorLodSystem] Failed to insert into the PlayerLoop: the Update phase was not found.");
            }
        }

        private static void RemovePlayerLoop()
        {
            if (!s_loopInstalled)
            {
                return;
            }

            var root = PlayerLoop.GetCurrentPlayerLoop();
            if (Remove(ref root, typeof(AnimatorLodSystem)))
            {
                PlayerLoop.SetPlayerLoop(root);
            }
            s_loopInstalled = false;
        }

        /// <summary>phase 段の末尾に system を追加する。</summary>
        private static bool AppendToPhase(ref PlayerLoopSystem root, System.Type phase, PlayerLoopSystem system)
        {
            if (root.subSystemList == null)
            {
                return false;
            }

            for (int i = 0; i < root.subSystemList.Length; i++)
            {
                if (root.subSystemList[i].type != phase)
                {
                    continue;
                }

                var phaseSystem = root.subSystemList[i];
                var list = new List<PlayerLoopSystem>(phaseSystem.subSystemList ?? System.Array.Empty<PlayerLoopSystem>());
                // 既に入っていれば二重挿入しない
                list.RemoveAll(s => s.type == system.type);
                list.Add(system);
                phaseSystem.subSystemList = list.ToArray();
                root.subSystemList[i] = phaseSystem;
                return true;
            }
            return false;
        }

        private static bool Remove(ref PlayerLoopSystem root, System.Type type)
        {
            if (root.subSystemList == null)
            {
                return false;
            }

            bool removed = false;
            for (int i = 0; i < root.subSystemList.Length; i++)
            {
                var sub = root.subSystemList[i];
                if (sub.subSystemList == null)
                {
                    continue;
                }
                var list = new List<PlayerLoopSystem>(sub.subSystemList);
                if (list.RemoveAll(s => s.type == type) > 0)
                {
                    sub.subSystemList = list.ToArray();
                    root.subSystemList[i] = sub;
                    removed = true;
                }
            }
            return removed;
        }

        // ---- 登録 ----

        // 個体が自分の位置(Schedule.Index)を持ち、解除は末尾との入れ替えで行う(登録・解除とも O(1))。
        // 並び順は変わるが、Tick は順序に依存しない。

        internal static void Register(AnimatorLod optimizer)
        {
            if (optimizer.Schedule.Index >= 0)
            {
                return;
            }
            var state = s_state;
            var instances = state.Instances;
            var animator = optimizer.Animator;
            optimizer.Schedule = new InstanceSchedule
            {
                Index = instances.Count,
                LastEvaluatedFrame = Time.frameCount,
                FirstEvaluationPending = true,
                AppliedSpeed = float.NaN,
                AppliedEnabled = animator.enabled,
            };
            instances.Add(optimizer);
            // 無効な間の即時評価要求は捨てる(登録後の初回は FirstEvaluationPending で必ず評価される。
            // 残すと ForceBucket でその位相に入り、一斉に有効化した個体の分散が崩れる)
            optimizer.ConsumeImmediateRequest();
            if (!state.FixedUpdateModeWarned && animator.updateMode == AnimatorUpdateMode.Fixed)
            {
                state.FixedUpdateModeWarned = true;
                Debug.LogWarning($"[AnimatorLod] '{optimizer.name}': Animator Update Mode 'Fixed' (Animate Physics) is not supported. " +
                                 "The Animator is evaluated in FixedUpdate, so the frame-based interval and speed compensation drift. " +
                                 "Use Normal or Unscaled Time. (Logged once per Play; other instances may also be affected.)", optimizer);
            }
            // 通常は ResetStatics で挿入済みで何もしない。static が初期化された場合(Play 中のスクリプト再コンパイル等)に挿入し直す
            InstallPlayerLoop();
        }

        internal static void Unregister(AnimatorLod optimizer)
        {
            int index = optimizer.Schedule.Index;
            if (index < 0)
            {
                return;
            }
            // ResetState が登録中の個体を必ず未登録に戻すので、Index >= 0 なら現在のリストに居る
            var instances = s_state.Instances;
            Debug.Assert(ReferenceEquals(instances[index], optimizer));

            ReleaseBucket(optimizer);
            optimizer.Schedule.Index = -1;
            int last = instances.Count - 1;
            if (index != last)
            {
                var moved = instances[last];
                instances[index] = moved;
                moved.Schedule.Index = index;
            }
            instances.RemoveAt(last);
        }

        // ---- 毎フレーム ----

        private static void Tick()
        {
            var state = s_state;
            var instances = state.Instances;
            if (!Application.isPlaying)
            {
                return;
            }
            if (instances.Count == 0)
            {
                // 最後の個体が解除された後も直前の集計が残らないようにする
                state.InvisibleCount = 0;
                state.EnabledThisFrame = 0;
                return;
            }

            int frame = Time.frameCount;
            if (frame == state.LastTickFrame)
            {
                return;
            }
            state.LastTickFrame = frame;

            var ctx = new TickContext { Frame = frame, MainView = UpdateMainCamera(state) };
            int invisible = 0;
            int enabledCount = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                var opt = instances[i];
                if (UpdateInstance(opt, ref ctx))
                {
                    enabledCount++;
                }
                if (opt.IsInvisible)
                {
                    invisible++;
                }
            }

            state.InvisibleCount = invisible;
            state.EnabledThisFrame = enabledCount;
        }

        /// <summary>1 個体の LOD・可視・Interval を判定して Animator を切り替える。このフレームに評価させたら true。</summary>
        private static bool UpdateInstance(AnimatorLod opt, ref TickContext ctx)
        {
            int frame = ctx.Frame;

#if UNITY_EDITOR
            opt.EditorApplyInspectorChanges();
#endif

            if (!opt.LodEnabled)
            {
                if (opt.HasLodState)
                {
                    opt.ResetRuntimeState();
                }
                // 毎フレーム評価するので即時評価要求は不要。残すと LOD を有効に戻したときに不要な強制評価になる
                opt.ConsumeImmediateRequest();
                ReleaseBucket(opt);
                EvaluateThisFrame(opt, frame);
                return true;
            }

            // 1. Screen Size 比 → LOD / 比率カリング
            opt.EvaluateLod(ResolveView(opt, ref ctx));

            // 2. 可視判定(Renderer.isVisible。Animator のカリングと同じ可視判定)
            bool visible = opt.ComputeVisible();
            opt.IsInvisible = !visible;

            // 3. Interval 決定: 不可視、または Culled 比率未満(LOD カリング)は Invisible Interval を絶対値で適用
            int interval = visible && !opt.IsCulled
                ? opt.GetEffectiveInterval(opt.CurrentLod)
                : opt.InvisibleInterval;
            interval = Mathf.Clamp(interval, 0, MaxEffectiveInterval);
            opt.CurrentInterval = interval;

            // 4. 即時評価要求: 評価したフレームを以降の間引き位相の起点にする
            if (opt.ConsumeImmediateRequest())
            {
                if (interval > 1)
                {
                    ForceBucket(opt, interval, frame % interval);
                }
                else
                {
                    ReleaseBucket(opt);
                }
                EvaluateThisFrame(opt, frame);
                return true;
            }

            // 5. 毎フレーム更新
            if (interval <= 1)
            {
                ReleaseBucket(opt);
                EvaluateThisFrame(opt, frame);
                return true;
            }

            // 6. N フレームに 1 回(位相バケット)。登録後の初回だけはバケットに関わらず評価させる
            AssignBucket(opt, interval);
            if (frame % interval != opt.Schedule.Bucket && !opt.Schedule.FirstEvaluationPending)
            {
                SetEnabled(opt, false);
                return false;
            }
            EvaluateThisFrame(opt, frame);
            return true;
        }

        /// <summary>このフレームに Animator を評価させる(speed = Speed × 経過フレーム数)。</summary>
        private static void EvaluateThisFrame(AnimatorLod opt, int frame)
        {
            int elapsed = Mathf.Clamp(frame - opt.Schedule.LastEvaluatedFrame, 1, MaxElapsedFrames);
            opt.Schedule.LastEvaluatedFrame = frame;
            opt.Schedule.FirstEvaluationPending = false;
            SetEnabled(opt, true);
            SetSpeed(opt, opt.Speed * elapsed);
        }

        /// <summary>値が変わったときだけ Animator.enabled を設定する。</summary>
        private static void SetEnabled(AnimatorLod opt, bool enabled)
        {
            if (opt.Schedule.AppliedEnabled != enabled)
            {
                opt.Animator.enabled = enabled;
                opt.Schedule.AppliedEnabled = enabled;
            }
        }

        /// <summary>値が変わったときだけ Animator.speed を設定する。</summary>
        private static void SetSpeed(AnimatorLod opt, float speed)
        {
            if (opt.Schedule.AppliedSpeed != speed)
            {
                opt.Animator.speed = speed;
                opt.Schedule.AppliedSpeed = speed;
            }
        }

        // ---- 位相バケット ----

        private static void AssignBucket(AnimatorLod opt, int interval)
        {
            if (opt.Schedule.Interval == interval)
            {
                return;
            }

            ReleaseBucket(opt);

            var counts = s_state.BucketCounts[interval];
            int best = 0;
            for (int b = 1; b < counts.Length; b++)
            {
                if (counts[b] < counts[best])
                {
                    best = b;
                }
            }
            counts[best]++;
            opt.Schedule.Interval = interval;
            opt.Schedule.Bucket = best;
        }

        private static void ForceBucket(AnimatorLod opt, int interval, int bucket)
        {
            ReleaseBucket(opt);
            s_state.BucketCounts[interval][bucket]++;
            opt.Schedule.Interval = interval;
            opt.Schedule.Bucket = bucket;
        }

        private static void ReleaseBucket(AnimatorLod opt)
        {
            // Interval は AssignBucket / ForceBucket(Clamp 済みの値)でしか入らず、Register で 0 に戻る
            int n = opt.Schedule.Interval;
            if (n > 1)
            {
                var counts = s_state.BucketCounts[n];
                Debug.Assert(counts[opt.Schedule.Bucket] > 0);
                counts[opt.Schedule.Bucket]--;
            }
            opt.Schedule.Interval = 0;
            opt.Schedule.Bucket = 0;
        }

        // ---- カメラ ----

        /// <summary>Camera.main の LodView(無ければ HasCamera = false)。</summary>
        private static AnimatorLod.LodView UpdateMainCamera(State state)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                state.MainCamera = null;
                state.MainCameraTransform = null;
                return default;
            }
            if (!ReferenceEquals(cam, state.MainCamera))
            {
                state.MainCamera = cam;
                state.MainCameraTransform = cam.transform;
            }
            return new AnimatorLod.LodView(cam, state.MainCameraTransform);
        }

        /// <summary>個体の LodCamera(未設定なら Camera.main)の LodView。</summary>
        private static AnimatorLod.LodView ResolveView(AnimatorLod opt, ref TickContext ctx)
        {
            var lodCamera = opt.LodCamera;
            if (lodCamera == null)
            {
                return ctx.MainView;
            }
            if (!ReferenceEquals(lodCamera, ctx.LodCamera))
            {
                ctx.LodCamera = lodCamera;
                ctx.LodCameraView = new AnimatorLod.LodView(lodCamera, opt.LodCameraTransform);
            }
            return ctx.LodCameraView;
        }

        // ---- Skin Weights キャッシュ ----

        /// <summary>mesh について mask の Skin Weights が温め済みか(null は true)。</summary>
        internal static bool IsSkinWeightsWarmed(Mesh mesh, int mask)
        {
            if (mesh == null)
            {
                return true;
            }
            s_state.WarmedSkinWeights.TryGetValue(mesh, out int done);
            return (mask & ~done) == 0;
        }

        /// <summary>BakeMesh で mesh の Skin Weights のキャッシュを事前に作る(smr の sharedMesh と quality は呼び出し側で戻す)。</summary>
        internal static void WarmSkinWeights(SkinnedMeshRenderer smr, Mesh mesh, int mask)
        {
            if (mesh == null)
            {
                return;
            }
            var warmed = s_state.WarmedSkinWeights;
            warmed.TryGetValue(mesh, out int done);
            int todo = mask & ~done;
            if (todo == 0)
            {
                return;
            }

            if (s_bakeScratch == null)
            {
                s_bakeScratch = new Mesh { name = "AnimatorLod.BakeScratch", hideFlags = HideFlags.HideAndDontSave };
            }
            if (smr.sharedMesh != mesh)
            {
                smr.sharedMesh = mesh;
            }
            for (int q = 1; q <= 4; q <<= 1)
            {
                if ((todo & (1 << q)) != 0)
                {
                    smr.quality = (SkinQuality)q;
                    smr.BakeMesh(s_bakeScratch);
                }
            }
            warmed[mesh] = done | todo;
        }
    }
}
