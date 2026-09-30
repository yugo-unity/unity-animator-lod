using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace AnimatorLodTest
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
            /// <summary>最後に評価したフレームの Time.timeAsDouble / unscaledTimeAsDouble(Update Mode に合わせて片方を使う)。</summary>
            public double LastEvaluatedTime;
            public double LastEvaluatedUnscaledTime;
            /// <summary>登録後まだ評価していない(初回はバケットに関わらず評価する)。</summary>
            public bool FirstEvaluationPending;
            /// <summary>最後に設定した Animator.speed。NaN は未設定。</summary>
            public float AppliedSpeed;
            /// <summary>最後に設定した Animator.enabled。</summary>
            public bool AppliedEnabled;

            public static InstanceSchedule Unregistered => new InstanceSchedule { Index = -1 };
        }

        /// <summary>
        /// 温めた Mesh の記録。Mesh は弱参照で持つ。static から強参照すると Resources.UnloadUnusedAssets が
        /// 使用中とみなし、キャラクターが居なくなっても Mesh(と GPU バッファ)がアンロードされないため。
        /// </summary>
        private sealed class WarmedMesh
        {
            public readonly System.WeakReference<Mesh> Mesh;
            /// <summary>温めた Skin Weights のビットマスク(<see cref="SkinWeightsBit"/>)。</summary>
            public int Bits;

            public WarmedMesh(Mesh mesh)
            {
                Mesh = new System.WeakReference<Mesh>(mesh);
            }

            /// <summary>記録した Mesh がまだ読み込まれているか(アンロード・破棄済み、または C# 側が回収済みなら false)。</summary>
            public bool IsAlive(out Mesh mesh) => Mesh.TryGetTarget(out mesh) && mesh != null;
        }

        /// <summary>Play 単位の状態(作り直すとリセットされる)。</summary>
        private sealed class State
        {
            public readonly List<AnimatorLod> Instances = new List<AnimatorLod>(1024);
            public readonly int[][] BucketCounts = CreateBucketCounts();
            /// <summary>Mesh の instance ID ごとに温めた Skin Weights(<see cref="WarmedMesh"/>)。</summary>
            public readonly Dictionary<int, WarmedMesh> WarmedSkinWeights = new Dictionary<int, WarmedMesh>();
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
            // Tick ごとに 1 回だけ読む時刻(個体ごとに読むとネイティブ呼び出しが個体数ぶん増える)
            public double Now;
            public double UnscaledNow;
            public float DeltaTime;
            public float UnscaledDeltaTime;
            public AnimatorLod.LodView MainView;
            // 直前の個体の LodCamera とその LodView(同じカメラが続く間は計算を使い回す)
            public Camera LodCamera;
            public AnimatorLod.LodView LodCameraView;
        }

        private static State s_state = new State();
        // 以下は Play 単位ではなく、PlayerLoop / Editor に残るオブジェクトの状態を映す
        private static bool s_loopInstalled;

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
                LastEvaluatedTime = Time.timeAsDouble,
                LastEvaluatedUnscaledTime = Time.unscaledTimeAsDouble,
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
                                 "The Animator is evaluated in FixedUpdate, so the per-frame throttling and speed compensation do not match it. " +
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

            var ctx = new TickContext
            {
                Frame = frame,
                Now = Time.timeAsDouble,
                UnscaledNow = Time.unscaledTimeAsDouble,
                DeltaTime = Time.deltaTime,
                UnscaledDeltaTime = Time.unscaledDeltaTime,
                MainView = UpdateMainCamera(state),
            };
            int invisible = 0;
            int enabledCount = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                var opt = instances[i];
                if (UpdateInstance(opt, i, ref ctx))
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
        private static bool UpdateInstance(AnimatorLod opt, int index, ref TickContext ctx)
        {
            int frame = ctx.Frame;

#if UNITY_EDITOR
            opt.EditorApplyInspectorChanges();
#endif

            bool lodActive = opt.LodEnabled || opt.MeshLodEnabled || opt.SkinWeightsLodEnabled;
            // 設定の変更後は間引きの位相に関わらず次の Tick で LOD を引き直す
            bool lodRefresh = opt.ConsumeLodRefresh();
            if (!lodActive && opt.HasLodState)
            {
                opt.ResetRuntimeState();
            }

            if (!opt.LodEnabled)
            {
                // Animation LOD が無効なら間引かず毎フレーム評価する。
                // Mesh LOD / Skin Weights の LOD 判定は LodEvaluationInterval フレームに 1 回へ分散する(切替は最大でその分遅れる)
                if (lodActive && (lodRefresh || !opt.HasEvaluatedLod || (frame + index) % opt.LodEvaluationInterval == 0))
                {
                    opt.EvaluateLod(ResolveView(opt, ref ctx));
                }
                opt.IsInvisible = false;
                opt.CurrentInterval = 0;
                // 毎フレーム評価するので即時評価要求は不要。残すと LOD を有効に戻したときに不要な強制評価になる
                opt.ConsumeImmediateRequest();
                ReleaseBucket(opt);
                EvaluateThisFrame(opt, ctx);
                return true;
            }

            // 1. 可視判定(Renderer.isVisible。Animator のカリングと同じ可視判定)。軽いので毎フレーム行い、画面に入った個体をすぐ起こす
            bool visible = opt.ComputeVisible();
            bool visibilityChanged = visible == opt.IsInvisible;
            opt.IsInvisible = !visible;
            bool immediate = opt.ConsumeImmediateRequest();

            // 2. Screen Size 比 → LOD / 比率カリング。Animator を評価するフレームだけ引き直す
            //    (評価するかは前回の Interval とバケットで決まる。間引き中の LOD の変化は次の評価フレームで反映され、最大 Interval フレーム遅れる)
            ref var schedule = ref opt.Schedule;
            if (lodRefresh || immediate || visibilityChanged || !opt.HasEvaluatedLod
                || schedule.FirstEvaluationPending || schedule.Interval <= 1 || frame % schedule.Interval == schedule.Bucket)
            {
                opt.EvaluateLod(ResolveView(opt, ref ctx));
            }

            // 3. Interval 決定: 不可視、または Culled 比率未満(LOD カリング)は Invisible Interval を絶対値で適用
            int interval = visible && !opt.IsCulled
                ? opt.GetEffectiveInterval(opt.CurrentLod)
                : opt.InvisibleInterval;
            interval = Mathf.Clamp(interval, 0, MaxEffectiveInterval);
            opt.CurrentInterval = interval;

            // 4. 即時評価要求: 評価したフレームを以降の間引き位相の起点にする
            if (immediate)
            {
                if (interval > 1)
                {
                    ForceBucket(opt, interval, frame % interval);
                }
                else
                {
                    ReleaseBucket(opt);
                }
                EvaluateThisFrame(opt, ctx);
                return true;
            }

            // 5. 毎フレーム更新
            if (interval <= 1)
            {
                ReleaseBucket(opt);
                EvaluateThisFrame(opt, ctx);
                return true;
            }

            // 6. N フレームに 1 回(位相バケット)。登録後の初回だけはバケットに関わらず評価させる
            AssignBucket(opt, interval);
            if (frame % interval != opt.Schedule.Bucket && !opt.Schedule.FirstEvaluationPending)
            {
                SetEnabled(opt, false);
                return false;
            }
            EvaluateThisFrame(opt, ctx);
            return true;
        }

        /// <summary>
        /// このフレームに Animator を評価させる。Animator はこのフレームの deltaTime × speed だけ進むので、
        /// speed = Speed × (前回の評価からの経過時間 / このフレームの deltaTime) にして、間引いたフレームの実時間を補償する
        /// (フレームレートが変動しても、間引いた各フレームの deltaTime の合計がそのまま進む)。
        /// </summary>
        private static void EvaluateThisFrame(AnimatorLod opt, in TickContext ctx)
        {
            ref var schedule = ref opt.Schedule;
            int frames = ctx.Frame - schedule.LastEvaluatedFrame;
            float scale;
            if (frames <= 1)
            {
                // 毎フレーム評価(と登録直後)は補償不要。比率を計算すると誤差で speed が毎回変わり、設定し直しが増える
                scale = 1f;
            }
            else if (frames > MaxElapsedFrames)
            {
                // 通常は起きない(評価の間隔は最大でも MaxElapsedFrames)。念のため補償量に上限を設ける
                scale = MaxElapsedFrames;
            }
            else
            {
                // Update Mode は間引いた評価のときだけ読む(毎フレーム評価の個体にネイティブ呼び出しを増やさない)。Fixed は非対応のため通常の時刻を使う
                bool unscaled = opt.Animator.updateMode == AnimatorUpdateMode.UnscaledTime;
                double elapsed = unscaled ? ctx.UnscaledNow - schedule.LastEvaluatedUnscaledTime : ctx.Now - schedule.LastEvaluatedTime;
                float deltaTime = unscaled ? ctx.UnscaledDeltaTime : ctx.DeltaTime;
                // deltaTime が 0(timeScale = 0 など)のときは Animator も進まないので補償しない
                scale = deltaTime > 0f ? Mathf.Max(1f, (float)(elapsed / deltaTime)) : 1f;
                // フレームレートが一定なら比率は経過フレーム数に一致する。誤差だけの差なら整数に揃え、speed の設定し直しを避ける
                if (Mathf.Abs(scale - frames) < frames * 1e-4f)
                {
                    scale = frames;
                }
            }
            schedule.LastEvaluatedFrame = ctx.Frame;
            schedule.LastEvaluatedTime = ctx.Now;
            schedule.LastEvaluatedUnscaledTime = ctx.UnscaledNow;
            schedule.FirstEvaluationPending = false;
            SetEnabled(opt, true);
            SetSpeed(opt, opt.Speed * scale);
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

        /// <summary>
        /// mesh を quality で描画するときにスキニングで使われる Skin Weights。
        /// エンジンと同じく renderer の quality(Auto は上限なし)・QualitySettings.skinWeights・Mesh が持つ本数の最小を取る。
        /// </summary>
        internal static SkinWeights ResolveSkinWeights(Mesh mesh, SkinQuality quality)
        {
            if (mesh == null)
            {
                return SkinWeights.None;
            }
            int rendererMax = quality == SkinQuality.Auto ? (int)SkinWeights.Unlimited : (int)quality;
            int qualityMax = (int)QualitySettings.skinWeights;
            int available = (int)mesh.skinWeightBufferLayout;
            return (SkinWeights)Mathf.Min(available, Mathf.Min(rendererMax, qualityMax));
        }

        private static int SkinWeightsBit(SkinWeights layout)
        {
            switch (layout)
            {
                case SkinWeights.OneBone: return 1;
                case SkinWeights.TwoBones: return 2;
                case SkinWeights.FourBones: return 4;
                case SkinWeights.Unlimited: return 8;
                default: return 0;
            }
        }

        /// <summary>
        /// mesh の layout 用のボーンウェイトの GPU バッファを事前に作る(GPU スキニングが初回に作るものと同じ)。
        /// 返る GraphicsBuffer は Mesh のバッファを参照するだけなので、Dispose しても Mesh 側のバッファは残る。
        /// </summary>
        internal static void WarmSkinWeights(Mesh mesh, SkinWeights layout)
        {
            int bit = SkinWeightsBit(layout);
            if (mesh == null || bit == 0)
            {
                return;
            }
            var warmed = s_state.WarmedSkinWeights;
            int id = mesh.GetInstanceID();
            if (warmed.TryGetValue(id, out var entry))
            {
                // 同じ instance ID でも記録した C# オブジェクトと違えば、アンロード後に読み直された(Resources は同じ ID で読み直される)か
                // C# 側が回収された。前者は GPU バッファも破棄されているので温め直す(後者はバッファが残っているので温め直しは安い)
                if (!entry.IsAlive(out var recorded) || !ReferenceEquals(recorded, mesh))
                {
                    entry.Mesh.SetTarget(mesh);
                    entry.Bits = 0;
                }
            }
            else
            {
                // 新しい Mesh を記録する前に、アンロード・破棄済みの Mesh の記録を捨てる
                // (記録は読み込まれている Mesh の数 + 前回の追加以降にアンロードされた数までに収まる)
                RemoveUnloadedMeshes(warmed);
                entry = new WarmedMesh(mesh);
                warmed.Add(id, entry);
            }
            if ((entry.Bits & bit) != 0)
            {
                return;
            }

            mesh.GetBoneWeightBuffer(layout)?.Dispose();
            entry.Bits |= bit;
        }

        // RemoveUnloadedMeshes の作業用(呼ぶたびに確保しない)
        private static readonly List<int> s_unloadedMeshes = new List<int>();

        private static void RemoveUnloadedMeshes(Dictionary<int, WarmedMesh> warmed)
        {
            foreach (var pair in warmed)
            {
                if (!pair.Value.IsAlive(out _))
                {
                    s_unloadedMeshes.Add(pair.Key);
                }
            }
            for (int i = 0; i < s_unloadedMeshes.Count; i++)
            {
                warmed.Remove(s_unloadedMeshes[i]);
            }
            s_unloadedMeshes.Clear();
        }
    }
}
