using UnityEngine;

namespace AnimatorLodTest
{
    /// <summary>
    /// Animator を持つ GameObject に 1 つずつ付ける個体単位の最適化コンポーネント
    /// (Animation LOD / Mesh LOD / Skin Weights LOD / Dither Fade)。毎フレームの判定と切替は <see cref="AnimatorLodSystem"/> が行う。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator))]
    public sealed class AnimatorLod : MonoBehaviour
    {
        public const int MaxInterval = 4;
        public const float MinRatio = 0.0001f;
        /// <summary>Animation LOD が無効な間の LOD 判定間隔(<see cref="LodEvaluationInterval"/>)の最大。</summary>
        public const int MaxLodEvaluationInterval = MaxInterval * 2;
        /// <summary>Dither Fade のフェード時間の既定(LODGroup.crossFadeAnimationDuration の既定と同じ)。</summary>
        public const float DefaultFadeDuration = 0.5f;
        /// <summary>フェード用マテリアルで有効にするシェーダーキーワード(AnimatorLodDitherFade.hlsl と対)。</summary>
        public const string DitherFadeKeyword = "ANIMATOR_LOD_DITHER_FADE";
        /// <summary>RSUV のうちフェード量に使うビット(下位 8 bit。0 = 完全表示、255 = 完全に消えた)。</summary>
        public const uint DitherFadeValueMask = 0xFFu;

        // ---- References(Editor で準備する。ランタイムでは探索しない) ----
        [SerializeField, Tooltip("Target Animator. Prepared in the Editor.")]
        private Animator animator;

        /// <summary>SMR ごとの事前準備データ。元の値は各機能を無効にしたときの復元先。</summary>
        [System.Serializable]
        private struct RendererBinding
        {
            public SkinnedMeshRenderer renderer;
            [Tooltip("Element i is the mesh for LOD i. Empty = original mesh.")]
            public Mesh[] lodMeshes;
            public Mesh mesh;
            public SkinQuality quality;
            // Dither Fade 用(ランタイムのみ)。fadeSource は差し替え前の sharedMaterials、fadeMaterials はフェード中の差し替え先。
            // fadeMaterials が null なら差し替えない(フェード用マテリアルに対応するシェーダーが無い)
            [System.NonSerialized] public Material[] fadeSource;
            [System.NonSerialized] public Material[] fadeMaterials;
        }

        [SerializeField, Tooltip("Target renderers and their prepared data. Prepared in the Editor.")]
        private RendererBinding[] renderers;

        // ---- Bounds ----
        [SerializeField, Tooltip("AABB in this transform's local space. Used as the bounding sphere for LOD.")]
        private Bounds lodBounds = new Bounds(new Vector3(0f, 1f, 0f), new Vector3(2.5f, 2.5f, 2.5f));
        /// <summary>lodBounds をどこから求めたか。値は旧 bool boundsCalculated(0 = false, 1 = true)と互換。</summary>
        public enum BoundsSource
        {
            /// <summary>Renderer の現在の Bounds(Controller が無いときのアタッチ時など)。</summary>
            Renderers = 0,
            /// <summary>Controller の全クリップを実測。</summary>
            AllClips = 1,
            /// <summary>アニメーションを適用しないデフォルトポーズを実測。</summary>
            DefaultPose = 2,
        }

        [SerializeField, HideInInspector, UnityEngine.Serialization.FormerlySerializedAs("boundsCalculated")]
        private BoundsSource boundsSource;

        // ---- Animation LOD ----
        [SerializeField]
        private bool lodEnabled = true;
        [SerializeField, Tooltip("Camera for the screen-size ratio. Empty = Camera.main.")]
        private Camera lodCamera;
        [SerializeField, Range(1, MaxLodEvaluationInterval),
         Tooltip("Evaluate LOD for Mesh LOD and Skin Weights every N frames (spread across instances). 1 = every frame. " +
                 "Not used while Animation LOD is enabled: LOD is then evaluated on the frames the Animator is updated.")]
        private int lodEvaluationInterval = 4;
        // 配列フィールドは初期化子を持たない(Instantiate のたびにコンストラクタで確保され、デシリアライズで上書きされて捨てられるため)。
        // 既定値は Reset(Editor)で与える。Reset を経ないランタイムの AddComponent は非対応(Awake でエラーにする)。
        private static readonly float[] DefaultLodRatios = { 0.5f, 0.25f, 0.125f };
        private static readonly int[] DefaultLodIntervals = { 1, 2, 3 };
        private static readonly SkinQuality[] DefaultLodSkinQualities = { SkinQuality.Auto, SkinQuality.Auto, SkinQuality.Auto, SkinQuality.Auto };

        [SerializeField, Tooltip("Screen-size ratio at which each LOD is left. Descending.")]
        private float[] lodRatios;
        [SerializeField, Range(0, MaxInterval), Tooltip("Animator update interval (frames) added to every LOD. 0/1 = every frame.")]
        private int baseInterval = 1;
        [SerializeField, Range(0, MaxInterval), Tooltip("Interval used when not visible or culled. Overrides LOD and Base.")]
        private int invisibleInterval = 2;
        [SerializeField, Tooltip("Interval added to Base Interval. Element i is for LOD i+1.")]
        private int[] lodIntervals;
        [SerializeField, Range(0f, 1f), Tooltip("Below this ratio the Invisible Interval applies. 0 = off.")]
        private float cullRatio;

        // ---- Dither Fade(Near / Far) ----
        [SerializeField, Tooltip("Dither-fade out below Culled and within Near Cull Distance, then stop rendering. " +
                                 "Needs LOD Cross Fade in the URP Asset and a material whose shader supports " + DitherFadeKeyword + ".")]
        private bool ditherFadeEnabled;
        [SerializeField, Min(0f), Tooltip("Dither fade time in seconds (scaled time, like LODGroup). 0 = switch immediately.")]
        private float fadeDuration = DefaultFadeDuration;
        [SerializeField, Min(0f), Tooltip("Fade out when the LOD Camera is closer than this distance (m) to the bounding sphere center. 0 = off.")]
        private float nearCullDistance;

        // ---- Mesh LOD / Skin Weights LOD(検証用) ----
        // Mesh LOD の差し替え先は RendererBinding.lodMeshes に持つ
        [SerializeField, Tooltip("Swap SkinnedMeshRenderer.sharedMesh to a reduced mesh per LOD.")]
        private bool meshLodEnabled;
        [SerializeField, Tooltip("Switch SkinnedMeshRenderer.quality (bones per vertex) per LOD.")]
        private bool skinWeightsLodEnabled;
        [SerializeField, Tooltip("Element i is the skin weights for LOD i. Auto = renderer's own setting.")]
        private SkinQuality[] lodSkinQualities;

        /// <summary>LOD 判定に使うカメラの値(Scale は透視投影で tan(fov/2)、正射影で orthographicSize)。</summary>
        internal readonly struct LodView
        {
            public readonly bool HasCamera;
            public readonly bool Orthographic;
            public readonly Vector3 Position;
            public readonly float Scale;

            public LodView(Camera camera, Transform cameraTransform)
            {
                HasCamera = true;
                Orthographic = camera.orthographic;
                Position = cameraTransform.position;
                Scale = Orthographic
                    ? Mathf.Max(camera.orthographicSize, 1e-4f)
                    : Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            }
        }

        // -1 = 未適用。実行中の個体を Instantiate した複製は SMR が差し替え済みのことがあるため、初回は必ず SMR と照合する
        private int _appliedMeshLod = -1;
        private int _appliedSkinLod = -1;
        // Transform のマネージドラッパーを強参照で保持する。
        // transform プロパティ経由だとラッパーが GC 後に再生成され、散発的な GC.Alloc になる。
        private Transform _transform;
        private Transform _lodCameraTransform;
        private int _currentLod = -1;
        private float _screenRatio;
        private bool _culled;
        private bool _nearCulled;
        private bool _immediateRequested;

        private enum FadeState : byte
        {
            /// <summary>元のマテリアルで表示中(RSUV = 0)。</summary>
            Visible,
            /// <summary>フェード用マテリアルに差し替えてフェード中。</summary>
            Fading,
            /// <summary>フェードアウトが終わり forceRenderingOff で描画を止めている(元のマテリアルに戻し済み)。</summary>
            Hidden,
        }

        private FadeState _fadeState;
        // 1 = 完全表示、0 = 完全に消えた
        private float _fadeVisibility = 1f;
        private uint _appliedFadeValue;
        // 登録後の最初の判定では、フェードせずに結果へ合わせる(生成直後の個体が Culled ならフェードアウトさせない)
        private bool _fadeSnapPending = true;
        // 設定が変わったので、間引きの位相を待たずに次の Tick で LOD を判定し直す
        private bool _lodRefreshRequested;
        private float _speed = 1f;
        // Editor で準備されていない(Awake でエラーにして無効化した)。有効に戻されても動かさない
        private bool _unprepared;

        public bool LodEnabled => lodEnabled;
        public bool MeshLodEnabled => meshLodEnabled;
        public bool SkinWeightsLodEnabled => skinWeightsLodEnabled;
        /// <summary>Dither Fade の設定値。実際に動くのは URP の LOD Cross Fade も有効なとき(<see cref="DitherFadeSupported"/>)。</summary>
        public bool DitherFadeEnabled => ditherFadeEnabled;
        public BoundsSource LodBoundsSource => boundsSource;
        public Bounds LodBounds => lodBounds;
        public Camera LodCamera => lodCamera;
        /// <summary>LodCamera の Transform のキャッシュ。</summary>
        internal Transform LodCameraTransform => _lodCameraTransform;
        public int LodLevelCount => lodRatios.Length + 1;

        /// <summary>全 LOD に加算される更新間隔(フレーム)。</summary>
        public int BaseInterval
        {
            get => baseInterval;
            set => baseInterval = Mathf.Clamp(value, 0, MaxInterval);
        }

        /// <summary>不可視・Culled 時に LOD と Base の代わりに使う更新間隔(フレーム)。</summary>
        public int InvisibleInterval
        {
            get => invisibleInterval;
            set => invisibleInterval = Mathf.Clamp(value, 0, MaxInterval);
        }

        /// <summary>
        /// Animation LOD が無効な間、Mesh LOD / Skin Weights の LOD を判定する間隔(フレーム。1 = 毎フレーム)。
        /// Animation LOD が有効な間は参照しない(Animator を評価するフレームで判定する)。
        /// </summary>
        public int LodEvaluationInterval
        {
            get => lodEvaluationInterval;
            set => lodEvaluationInterval = Mathf.Clamp(value, 1, MaxLodEvaluationInterval);
        }

        /// <summary>Dither Fade のフェード時間(秒、スケールされた時間)。0 = 即時に切り替える。</summary>
        public float FadeDuration
        {
            get => fadeDuration;
            set => fadeDuration = Mathf.Max(0f, value);
        }

        /// <summary>LOD Camera が外接球の中心からこの距離(m)より近いと Dither Fade でフェードアウトする。0 = オフ。</summary>
        public float NearCullDistance
        {
            get => nearCullDistance;
            set
            {
                nearCullDistance = Mathf.Max(0f, value);
                _lodRefreshRequested = true;
            }
        }

        /// <summary>
        /// Dither Fade が使えるか(URP Asset の LOD Cross Fade。URP はパイプライン生成時に QualitySettings.enableLODCrossFade へ反映する)。
        /// 無効なら <see cref="DitherFadeEnabled"/> が true でも Dither Fade は動かない。
        /// </summary>
        public static bool DitherFadeSupported => QualitySettings.enableLODCrossFade;

        /// <summary>Animator の再生速度。有効な間は Animator.speed の代わりにこれを設定する。</summary>
        public float Speed
        {
            get => _speed;
            set
            {
                _speed = value;
                // 未登録の間は Animator を管理していないので直接反映する(次の OnEnable でそのまま取り込まれる)
                if (Schedule.Index < 0 && animator != null)
                {
                    animator.speed = value;
                }
            }
        }

        /// <summary>LOD level の有効な更新間隔(Base 加算済み)。</summary>
        public int GetEffectiveInterval(int level)
        {
            int add = level >= 1 && level - 1 < lodIntervals.Length ? lodIntervals[level - 1] : 0;
            return baseInterval + add;
        }

        /// <summary>現在の LOD。LOD 無効時は -1。Culled 時は LodLevelCount。</summary>
        public int CurrentLod => _culled ? LodLevelCount : _currentLod;
        /// <summary>直近の判定の Screen Size 比。</summary>
        public float CurrentScreenRatio => _screenRatio;
        /// <summary>直近の判定で Culled だったか。</summary>
        public bool IsCulled => _culled;
        /// <summary>直近の判定で LOD Camera が Near Cull Distance より近かったか。</summary>
        public bool IsNearCulled => _nearCulled;
        /// <summary>Dither Fade でフェード中か。</summary>
        public bool IsFading => _fadeState == FadeState.Fading;
        /// <summary>Dither Fade のフェードアウトが終わり、描画を止めているか。</summary>
        public bool IsFadeHidden => _fadeState == FadeState.Hidden;
        /// <summary>Dither Fade の表示量(1 = 完全表示、0 = 完全に消えた)。</summary>
        public float FadeVisibility => _fadeVisibility;
        /// <summary>直近の判定で不可視だったか。</summary>
        public bool IsInvisible { get; internal set; }
        /// <summary>直近の判定で適用した更新間隔。</summary>
        public int CurrentInterval { get; internal set; }
        /// <summary>割り当て中の位相バケット。</summary>
        public int CurrentBucket => Schedule.Bucket;

        // ---- AnimatorLodSystem 用の内部状態 ----
        internal Animator Animator => animator;
        /// <summary>LOD を判定済みか(ResetRuntimeState で未判定に戻る)。</summary>
        internal bool HasEvaluatedLod => _currentLod >= 0;
        internal AnimatorLodSystem.InstanceSchedule Schedule = AnimatorLodSystem.InstanceSchedule.Unregistered;
        /// <summary>ResetRuntimeState で戻すべき状態があるか。</summary>
        internal bool HasLodState => _currentLod >= 0 || _appliedMeshLod != 0 || _appliedSkinLod != 0 || _fadeState != FadeState.Visible;

        // Awake 以降のランタイム処理は次を前提とし、null チェックしない:
        //   animator / renderers / lod* 配列は非 null、lodMeshes は非 null(要素は null 可 = 元の Mesh)。
        //   Animator が破棄されるのは AnimatorLod と同時のみ(個別の破棄は想定しない)。
        //   SMR は個別に破棄されうる(装備の付け替え等)。renderers を走査する処理は先頭で RemoveMissingRenderers を呼び、破棄済みを除く。
        private void Awake()
        {
            _transform = transform;
            _lodCameraTransform = lodCamera != null ? lodCamera.transform : null;
            if (!IsPrepared)
            {
                _unprepared = true;
                Debug.LogError($"[AnimatorLod] '{name}': this component was not prepared in the Editor " +
                               "(for example, it was added with AddComponent at runtime), which is not supported. It is disabled. " +
                               "Add it in the Editor and put it on a prefab instead.", this);
                enabled = false;
                return;
            }
            RemoveMissingRenderers();
            animator.keepAnimatorStateOnDisable = true;
        }

        /// <summary>
        /// Editor で準備済みか。Reset(アタッチ時)が References と LOD テーブルを用意するので、
        /// それを経ないランタイムの AddComponent では Animator と LOD テーブルが空のまま残る。
        /// </summary>
        private bool IsPrepared => animator != null && lodRatios != null && lodRatios.Length > 0 && renderers != null;

        private void ApplyLadderDefaults()
        {
            lodRatios = (float[])DefaultLodRatios.Clone();
            lodIntervals = (int[])DefaultLodIntervals.Clone();
            lodSkinQualities = (SkinQuality[])DefaultLodSkinQualities.Clone();
        }

        /// <summary>破棄済みの SMR を renderers から除く。</summary>
        private void RemoveMissingRenderers()
        {
            int valid = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].renderer != null)
                {
                    valid++;
                }
            }
            if (valid == renderers.Length)
            {
                return;
            }

            Debug.LogWarning($"[AnimatorLod] '{name}': {renderers.Length - valid} Skinned Mesh Renderer(s) in References are missing or were destroyed. " +
                             "Ignoring them. If they were removed in the Editor, use 'Refresh References' in the Inspector to update the references.", this);
            var compacted = new RendererBinding[valid];
            int n = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].renderer != null)
                {
                    compacted[n++] = renderers[i];
                }
            }
            renderers = compacted;
        }

        /// <summary>SMR の現在の設定を元の値として記録した RendererBinding を作る。</summary>
        private RendererBinding CreateBinding(SkinnedMeshRenderer smr, Mesh[] lodMeshes)
        {
            var binding = new RendererBinding { renderer = smr, lodMeshes = lodMeshes };
            CaptureRendererState(ref binding);
            return binding;
        }

        private void CaptureRendererState(ref RendererBinding binding)
        {
            var smr = binding.renderer;
            if (smr == null)
            {
                return;
            }
            binding.mesh = smr.sharedMesh;
            binding.quality = smr.quality;
        }

        private void OnEnable()
        {
            if (_unprepared)
            {
                enabled = false;
                return;
            }
            WarmSkinWeightCaches();
            // Dither Fade が有効なら、最初のフェードの Tick で作らないよう今のうちにフェード用マテリアルを用意する
            if (ditherFadeEnabled && DitherFadeSupported)
            {
                PrepareFadeMaterials();
            }
            // 登録すると Animator.speed はシステムが書き換えるので、その前にユーザーの速度として取り込む
            _speed = animator.speed;
            AnimatorLodSystem.Register(this);
        }

        private void OnDisable()
        {
            if (_unprepared)
            {
                return;
            }
            AnimatorLodSystem.Unregister(this);
            ResetRuntimeState();
            if (!animator.enabled)
            {
                animator.enabled = true;
            }
            animator.speed = _speed;
        }

        // ---- 公開 API ----

        public void SetLodEnabled(bool enable)
        {
            lodEnabled = enable;
            _lodRefreshRequested = true;
        }

        /// <summary>Mesh LOD の有効/無効。無効化時は元の Mesh に戻す。</summary>
        public void SetMeshLodEnabled(bool enable)
        {
            meshLodEnabled = enable;
            _lodRefreshRequested = true;
            // 無効な間は SMR に触れない(OnDisable で元に戻し済みで、次に有効になった後の Tick で反映される)
            if (!isActiveAndEnabled)
            {
                return;
            }
            // 使う (Mesh, Skin Weights) の組が変わるので、足りない分を温める
            WarmSkinWeightCaches();
            if (!enable)
            {
                ApplyMeshLod(0);
            }
        }

        /// <summary>Skin Weights LOD の有効/無効。無効化時は元の quality に戻す。</summary>
        public void SetSkinWeightsLodEnabled(bool enable)
        {
            skinWeightsLodEnabled = enable;
            _lodRefreshRequested = true;
            // 無効な間は SMR に触れない(OnDisable で元に戻し済みで、キャッシュは次の OnEnable で温める)
            if (!isActiveAndEnabled)
            {
                return;
            }
            // 使う (Mesh, Skin Weights) の組が変わるので、足りない分を温める
            WarmSkinWeightCaches();
            if (!enable)
            {
                ApplySkinQuality(0);
            }
        }

        /// <summary>
        /// Dither Fade の有効/無効。無効化時はフェード中・描画停止中でも次の Tick で元の表示に戻す。
        /// 有効化してもフェード用マテリアルはここでは作らない(最初にフェードが起きたときに作る。先に用意するなら <see cref="Prewarm"/>)。
        /// </summary>
        public void SetDitherFadeEnabled(bool enable)
        {
            ditherFadeEnabled = enable;
            _lodRefreshRequested = true;
        }

        /// <summary>
        /// Mesh LOD / Skin Weights LOD が使うボーンウェイトの GPU バッファと、Dither Fade のフェード用マテリアルを今すぐ作る
        /// (同じ Mesh と本数の組、同じマテリアルは 1 回だけ)。
        /// 通常はコンポーネントが有効になったときに自動で行うが、そのフレームに作成の負荷が乗るため、
        /// ロード中などに呼んでおく。プレハブのアセットに対しても呼べる(インスタンスを作らず、アセットも書き換えない)。
        /// 実行中に QualitySettings.skinWeights を変えたあとに呼び直すと、新しく必要になった本数の分を作る。
        /// フェード用マテリアルは Dither Fade が有効(URP の LOD Cross Fade も有効)な Play 中だけ作る。
        /// </summary>
        public void Prewarm()
        {
            if (!IsPrepared)
            {
                return;
            }
            WarmSkinWeightCaches();
            if (ditherFadeEnabled && DitherFadeSupported)
            {
                WarmFadeMaterials();
            }
        }

        /// <summary>次の Tick で間引きに関わらず 1 回評価させる。外部から Animator のステートやパラメータを変えた直後に呼ぶ。</summary>
        public void RequestImmediateUpdate()
        {
            _immediateRequested = true;
        }

        // ---- Mesh LOD / Skin Weights LOD ----

        /// <summary>LOD level を Mesh LOD と Skin Weights LOD に反映する。</summary>
        private void ApplyLodOverrides(int level)
        {
            int tableLevel = Mathf.Clamp(level, 0, lodRatios.Length);
            ApplyMeshLod(meshLodEnabled ? tableLevel : 0);
            ApplySkinQuality(skinWeightsLodEnabled ? tableLevel : 0);
        }

        private void ApplyMeshLod(int level)
        {
            if (_appliedMeshLod == level)
            {
                return;
            }
            _appliedMeshLod = level;

            RemoveMissingRenderers();
            for (int i = 0; i < renderers.Length; i++)
            {
                var binding = renderers[i];
                var smr = binding.renderer;
                var mesh = GetLodMesh(binding, level);

                // sharedMesh を差し替えても localBounds はエンジン側で保持される
                if (smr.sharedMesh != mesh)
                {
                    smr.sharedMesh = mesh;
                }
            }
        }

        private void ApplySkinQuality(int level)
        {
            if (_appliedSkinLod == level)
            {
                return;
            }
            _appliedSkinLod = level;

            RemoveMissingRenderers();
            for (int i = 0; i < renderers.Length; i++)
            {
                var binding = renderers[i];
                var target = GetLodSkinQuality(binding, level);
                if (binding.renderer.quality != target)
                {
                    binding.renderer.quality = target;
                }
            }
        }

        /// <summary>level で使う Mesh(未設定の LOD と LOD 0 は元の Mesh)。</summary>
        private static Mesh GetLodMesh(in RendererBinding binding, int level)
        {
            Mesh mesh = null;
            if (level > 0 && level < binding.lodMeshes.Length)
            {
                mesh = binding.lodMeshes[level];
            }
            return mesh != null ? mesh : binding.mesh;
        }

        /// <summary>level で使う quality(Auto の LOD と LOD 0 は renderer 元の quality)。</summary>
        private SkinQuality GetLodSkinQuality(in RendererBinding binding, int level)
        {
            var quality = level > 0 && level < lodSkinQualities.Length
                ? lodSkinQualities[level]
                : SkinQuality.Auto;
            return quality == SkinQuality.Auto ? binding.quality : quality;
        }

        private void RestoreOriginalMeshAndQuality()
        {
            ApplyMeshLod(0);
            ApplySkinQuality(0);
        }

        /// <summary>
        /// 現在の Mesh LOD / Skin Weights LOD の設定で各 LOD が使う (Mesh, Skin Weights) の組について、
        /// ボーンウェイトの GPU バッファを事前に作る。LOD 0 と同じ組は通常の描画で作られるので除く。
        /// </summary>
        private void WarmSkinWeightCaches()
        {
            if (!meshLodEnabled && !skinWeightsLodEnabled)
            {
                return;
            }

            // Renderer 自体は使わない(記録済みの Mesh と quality だけを見る)ので RemoveMissingRenderers は呼ばない。
            // プレハブのアセットに対する Prewarm でも renderers を書き換えないようにするため
            int levels = lodRatios.Length + 1;
            for (int i = 0; i < renderers.Length; i++)
            {
                var binding = renderers[i];
                var baseLayout = AnimatorLodSystem.ResolveSkinWeights(binding.mesh, binding.quality);
                for (int level = 1; level < levels; level++)
                {
                    var mesh = GetLodMesh(binding, meshLodEnabled ? level : 0);
                    var layout = AnimatorLodSystem.ResolveSkinWeights(mesh, GetLodSkinQuality(binding, skinWeightsLodEnabled ? level : 0));
                    if (mesh == binding.mesh && layout == baseLayout)
                    {
                        continue;
                    }
                    AnimatorLodSystem.WarmSkinWeights(mesh, layout);
                }
            }
        }

        // ---- Dither Fade ----

        // GetSharedMaterials の受け取り用(呼ぶたびに確保しない。メインスレッドのみ)
        private static readonly System.Collections.Generic.List<Material> s_materialScratch = new System.Collections.Generic.List<Material>();

        /// <summary>
        /// Dither Fade を 1 フレーム進める(LOD 判定の後に Tick から毎フレーム呼ばれる)。
        /// active = Dither Fade の設定と URP の LOD Cross Fade がともに有効。無効なら元の表示に戻す。
        /// </summary>
        internal void UpdateDitherFade(bool active, float deltaTime)
        {
            if (!active)
            {
                if (_fadeState != FadeState.Visible)
                {
                    ShowImmediately();
                }
                return;
            }
            if (!HasEvaluatedLod)
            {
                return;
            }

            bool hide = _culled || _nearCulled;
            bool snap = _fadeSnapPending;
            _fadeSnapPending = false;
            switch (_fadeState)
            {
                case FadeState.Visible:
                    if (!hide)
                    {
                        return;
                    }
                    if (snap)
                    {
                        HideImmediately();
                        return;
                    }
                    BeginFade(1f);
                    break;
                case FadeState.Hidden:
                    if (hide)
                    {
                        return;
                    }
                    SetForceRenderingOff(false);
                    BeginFade(0f);
                    break;
            }

            // フェード中。向きが逆になっても今の表示量から戻る
            float step = fadeDuration > 0f ? deltaTime / fadeDuration : 1f;
            _fadeVisibility = Mathf.Clamp01(_fadeVisibility + (hide ? -step : step));
            if (hide && _fadeVisibility <= 0f)
            {
                HideImmediately();
            }
            else if (!hide && _fadeVisibility >= 1f)
            {
                ShowImmediately();
            }
            else
            {
                ApplyFadeValue((uint)Mathf.RoundToInt((1f - _fadeVisibility) * DitherFadeValueMask));
            }
        }

        /// <summary>フェード用マテリアルに差し替えてフェードを始める。</summary>
        private void BeginFade(float visibility)
        {
            RemoveMissingRenderers();
            for (int i = 0; i < renderers.Length; i++)
            {
                ref var binding = ref renderers[i];
                PrepareFadeMaterials(ref binding);
                if (binding.fadeMaterials != null)
                {
                    binding.renderer.sharedMaterials = binding.fadeMaterials;
                }
            }
            _fadeState = FadeState.Fading;
            _fadeVisibility = visibility;
        }

        /// <summary>元のマテリアルと RSUV に戻し、描画を止める(フェードアウトの完了)。</summary>
        private void HideImmediately()
        {
            // 表示中から直接止める(登録直後)ときは SMR が元のマテリアルのままなので触らない
            if (_fadeState == FadeState.Fading)
            {
                RestoreFadeMaterials();
            }
            SetForceRenderingOff(true);
            _fadeState = FadeState.Hidden;
            _fadeVisibility = 0f;
        }

        /// <summary>元のマテリアル・RSUV・描画に戻す(フェードインの完了、または Dither Fade の無効化)。</summary>
        private void ShowImmediately()
        {
            if (_fadeState == FadeState.Hidden)
            {
                SetForceRenderingOff(false);
            }
            else if (_fadeState == FadeState.Fading)
            {
                RestoreFadeMaterials();
            }
            _fadeState = FadeState.Visible;
            _fadeVisibility = 1f;
        }

        private void RestoreFadeMaterials()
        {
            RemoveMissingRenderers();
            for (int i = 0; i < renderers.Length; i++)
            {
                var binding = renderers[i];
                if (binding.fadeMaterials != null)
                {
                    binding.renderer.sharedMaterials = binding.fadeSource;
                }
            }
            ApplyFadeValue(0u);
        }

        private void SetForceRenderingOff(bool off)
        {
            RemoveMissingRenderers();
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].renderer.forceRenderingOff = off;
            }
        }

        /// <summary>RSUV の下位 8 bit にフェード量を書く(値が変わったときだけ)。上位 24 bit は予約で 0。</summary>
        private void ApplyFadeValue(uint value)
        {
            if (_appliedFadeValue == value)
            {
                return;
            }
            _appliedFadeValue = value;
            RemoveMissingRenderers();
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].renderer.SetShaderUserValue(value);
            }
        }

        /// <summary>全 SMR の現在の sharedMaterials に対するフェード用マテリアルの配列を用意する(OnEnable 用)。</summary>
        private void PrepareFadeMaterials()
        {
            RemoveMissingRenderers();
            for (int i = 0; i < renderers.Length; i++)
            {
                PrepareFadeMaterials(ref renderers[i]);
            }
        }

        /// <summary>
        /// binding の SMR の現在の sharedMaterials(元のマテリアル)に対するフェード用マテリアルの配列を用意する。
        /// 前回と同じマテリアルなら作り直さない(確保しない)。SMR が元のマテリアルを持っている間にだけ呼ぶ。
        /// </summary>
        private static void PrepareFadeMaterials(ref RendererBinding binding)
        {
            var current = s_materialScratch;
            binding.renderer.GetSharedMaterials(current);
            bool same = binding.fadeSource != null && SameMaterials(binding.fadeSource, current);
            // static のリストにマテリアルを残すと、使われなくなっても Resources.UnloadUnusedAssets で解放されないので空にする
            var source = same ? null : current.ToArray();
            current.Clear();
            if (same)
            {
                return;
            }

            var fade = new Material[source.Length];
            bool anyFade = false;
            for (int m = 0; m < source.Length; m++)
            {
                fade[m] = AnimatorLodSystem.GetFadeMaterial(source[m]);
                anyFade |= fade[m] != source[m];
            }
            binding.fadeSource = source;
            binding.fadeMaterials = anyFade ? fade : null;
        }

        private static bool SameMaterials(Material[] recorded, System.Collections.Generic.List<Material> current)
        {
            if (recorded.Length != current.Count)
            {
                return false;
            }
            for (int m = 0; m < recorded.Length; m++)
            {
                if (!ReferenceEquals(recorded[m], current[m]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 各 SMR の sharedMaterials に対するフェード用マテリアルを作っておく(<see cref="Prewarm"/> 用)。
        /// プレハブのアセットに対しても呼ばれるので、renderers(と各 binding の配列)は書き換えない。
        /// </summary>
        private void WarmFadeMaterials()
        {
            var current = s_materialScratch;
            for (int i = 0; i < renderers.Length; i++)
            {
                var smr = renderers[i].renderer;
                if (smr == null)
                {
                    continue;
                }
                smr.GetSharedMaterials(current);
                for (int m = 0; m < current.Count; m++)
                {
                    AnimatorLodSystem.GetFadeMaterial(current[m]);
                }
            }
            current.Clear();
        }

        // ---- AnimatorLodSystem から呼ばれる判定 ----

        /// <summary>Screen Size 比から LOD と Culled を、カメラ距離から Near Culled を決め、Mesh LOD と Skin Weights LOD に反映する。</summary>
        internal void EvaluateLod(in LodView view)
        {
            float ratio = ComputeScreenRatio(_transform.localToWorldMatrix, view, out float distance);
            _screenRatio = ratio;
            _culled = IsCulledRatio(ratio);
            _nearCulled = IsNearCulledDistance(distance);
            int level = LevelForRatio(ratio);
            _currentLod = level;

            ApplyLodOverrides(_culled ? lodRatios.Length : level);
        }

        /// <summary>外接球が画面高さに占める割合。カメラが無ければ 1。distance はカメラから外接球の中心までの距離(カメラが無ければ無限大)。</summary>
        private float ComputeScreenRatio(in Matrix4x4 localToWorld, in LodView view, out float distance)
        {
            if (!view.HasCamera)
            {
                distance = float.PositiveInfinity;
                return 1f;
            }
            // 中心と半径は localToWorld だけから求める(lossyScale + TransformPoint の 2 回のネイティブ呼び出しを避ける)
            float radius = LodSphereRadius(localToWorld);
            distance = Vector3.Distance(view.Position, localToWorld.MultiplyPoint3x4(lodBounds.center));
            float denom = view.Orthographic ? view.Scale : distance * view.Scale;
            return radius / Mathf.Max(denom, 1e-4f);
        }

        /// <summary>
        /// LOD 判定に使う外接球のワールド半径(lodBounds の外接球 × 各軸のワールドスケールの最大)。
        /// 各軸のワールドスケールは localToWorld の列ベクトルの長さで、せん断が無ければ lossyScale の絶対値と一致する。
        /// </summary>
        private float LodSphereRadius(in Matrix4x4 m)
        {
            float sx = m.m00 * m.m00 + m.m10 * m.m10 + m.m20 * m.m20;
            float sy = m.m01 * m.m01 + m.m11 * m.m11 + m.m21 * m.m21;
            float sz = m.m02 * m.m02 + m.m12 * m.m12 + m.m22 * m.m22;
            // 3 引数の Mathf.Max は Max(params float[]) に解決され float[3] を毎回確保するため、2 引数版を入れ子にする
            float scale = Mathf.Sqrt(Mathf.Max(Mathf.Max(sx, sy), sz));
            return lodBounds.extents.magnitude * scale;
        }

        private bool IsCulledRatio(float ratio) => cullRatio > 0f && ratio < cullRatio;

        private bool IsNearCulledDistance(float distance) => nearCullDistance > 0f && distance < nearCullDistance;

        private int LevelForRatio(float ratio)
        {
            for (int i = 0; i < lodRatios.Length; i++)
            {
                if (ratio >= lodRatios[i])
                {
                    return i;
                }
            }
            return lodRatios.Length;
        }

        /// <summary>References の SMR のいずれかが描画されていれば true(SMR が無ければ true)。</summary>
        internal bool ComputeVisible()
        {
            RemoveMissingRenderers();
            if (renderers.Length == 0)
            {
                return true;
            }
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].renderer.isVisible)
                {
                    return true;
                }
            }
            return false;
        }

        internal bool ConsumeImmediateRequest()
        {
            bool requested = _immediateRequested;
            _immediateRequested = false;
            return requested;
        }

        internal bool ConsumeLodRefresh()
        {
            bool requested = _lodRefreshRequested;
            _lodRefreshRequested = false;
            return requested;
        }

        /// <summary>LOD 判定の結果を捨て、Mesh・Skin Weights・Dither Fade の表示を元に戻す。</summary>
        internal void ResetRuntimeState()
        {
            _currentLod = -1;
            _culled = false;
            _nearCulled = false;
            IsInvisible = false;
            CurrentInterval = 0;
            RestoreOriginalMeshAndQuality();
            if (_fadeState != FadeState.Visible)
            {
                ShowImmediately();
            }
            _fadeSnapPending = true;
        }

#if UNITY_EDITOR
        // ================================================================
        // Editor 専用(Inspector からの編集・実測)。プレイヤービルドには含まれない。
        // ================================================================

        // Play 中に Inspector で値が変わった印。Inspector はフィールドを直接書き換え setter を経由しないため、次の Tick で反映する
        // (OnValidate は Awake より前にも呼ばれ、中で SMR を触るのも避けたいので、ここでは印だけ付ける)
        private bool _editorInspectorChanged;

        private void OnValidate()
        {
            NormalizeLadder();
            // 編集中は SMR が元の状態なので、元の値を取り直して常に最新に保つ
            // (Play 中は SMR をランタイムが書き換えているため触らず、EditorApplyInspectorChanges で必要な分だけ反映する)
            if (Application.isPlaying)
            {
                _editorInspectorChanged = true;
            }
            else
            {
                EditorRefreshRendererState();
            }
        }

        /// <summary>Play 中の Inspector での変更を反映する(Tick から呼ばれる)。</summary>
        internal void EditorApplyInspectorChanges()
        {
            if (!_editorInspectorChanged)
            {
                return;
            }
            _editorInspectorChanged = false;

            _lodCameraTransform = lodCamera != null ? lodCamera.transform : null;

            // 元の値は SMR が書き換わっているので取り直さない
            WarmSkinWeightCaches();
            // 未適用に戻し、次の判定でテーブルを引き直させる(LOD 無効なら次の Tick で元の Mesh / quality に戻る)
            _appliedMeshLod = -1;
            _appliedSkinLod = -1;
            _lodRefreshRequested = true;
        }

        /// <summary>Inspector 編集後に配列長・降順・範囲を整える。</summary>
        private void NormalizeLadder()
        {
            if (lodRatios == null || lodRatios.Length == 0)
            {
                lodRatios = new[] { 0.5f };
            }

            // 降順を保つ(隣を跨がない)
            float upper = 1f;
            for (int i = 0; i < lodRatios.Length; i++)
            {
                lodRatios[i] = Mathf.Clamp(lodRatios[i], MinRatio, upper);
                upper = lodRatios[i];
            }
            cullRatio = Mathf.Clamp(cullRatio, 0f, lodRatios[lodRatios.Length - 1]);

            baseInterval = Mathf.Clamp(baseInterval, 0, MaxInterval);
            invisibleInterval = Mathf.Clamp(invisibleInterval, 0, MaxInterval);
            lodEvaluationInterval = Mathf.Clamp(lodEvaluationInterval, 1, MaxLodEvaluationInterval);
            fadeDuration = Mathf.Max(0f, fadeDuration);
            nearCullDistance = Mathf.Max(0f, nearCullDistance);

            // lodIntervals は LOD 1..N 用(要素数 = 境界数)
            int boundaries = lodRatios.Length;
            if (lodIntervals == null || lodIntervals.Length != boundaries)
            {
                var resized = new int[boundaries];
                for (int i = 0; i < boundaries; i++)
                {
                    resized[i] = lodIntervals != null && i < lodIntervals.Length
                        ? lodIntervals[i]
                        : Mathf.Min(MaxInterval, i + 1);
                }
                lodIntervals = resized;
            }
            for (int i = 0; i < lodIntervals.Length; i++)
            {
                lodIntervals[i] = Mathf.Clamp(lodIntervals[i], 0, MaxInterval);
            }

            // Mesh LOD / Skin Weights のテーブルは LOD level 数(境界数 + 1)に揃える
            int levels = boundaries + 1;
            lodSkinQualities = ResizePreserve(lodSkinQualities, levels, SkinQuality.Auto);
            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    renderers[i].lodMeshes = ResizePreserve(renderers[i].lodMeshes, levels, null);
                }
            }
        }

        private static T[] ResizePreserve<T>(T[] source, int length, T fill)
        {
            if (source != null && source.Length == length)
            {
                return source;
            }
            var resized = new T[length];
            for (int i = 0; i < length; i++)
            {
                resized[i] = source != null && i < source.Length ? source[i] : fill;
            }
            return resized;
        }

        /// <summary>全 Renderer の元の値を取り直す。</summary>
        private void EditorRefreshRendererState()
        {
            if (renderers == null)
            {
                return;
            }
            for (int i = 0; i < renderers.Length; i++)
            {
                CaptureRendererState(ref renderers[i]);
            }
        }

        /// <summary>Generate Reduced Meshes の結果を書き込む。</summary>
        public void EditorSetLodMeshes(int index, Mesh[] lodMeshes)
        {
            renderers[index].lodMeshes = lodMeshes;
            NormalizeLadder();
        }

        /// <summary>準備済みの SMR の数。</summary>
        public int EditorRendererCount => renderers != null ? renderers.Length : 0;

        /// <summary>準備済みの index 番目の SMR。</summary>
        public SkinnedMeshRenderer EditorGetRenderer(int index) => renderers[index].renderer;

        /// <summary>準備済みの SMR の配列。</summary>
        public SkinnedMeshRenderer[] EditorSkinnedMeshRenderers
        {
            get
            {
                if (renderers == null)
                {
                    return System.Array.Empty<SkinnedMeshRenderer>();
                }
                var result = new SkinnedMeshRenderer[renderers.Length];
                for (int i = 0; i < renderers.Length; i++)
                {
                    result[i] = renderers[i].renderer;
                }
                return result;
            }
        }

        /// <summary>アタッチ時に既定値を与え、References と Bounds を準備する。</summary>
        private void Reset()
        {
            ApplyLadderDefaults();
            EditorFetchReferences();
            if (EditorResetBoundsHook != null)
            {
                EditorResetBoundsHook(this);
            }
            else if (EditorComputeRendererBounds(out var current))
            {
                EditorSetCalculatedBounds(current, BoundsSource.Renderers);
            }
        }

        /// <summary>References を取得し直す(割り当て済みの Mesh LOD は引き継ぐ)。</summary>
        public void EditorFetchReferences()
        {
            animator = GetComponent<Animator>();
            var smrs = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var previous = renderers;
            renderers = new RendererBinding[smrs.Length];
            for (int i = 0; i < smrs.Length; i++)
            {
                renderers[i] = CreateBinding(smrs[i], FindLodMeshes(previous, smrs[i]));
            }
            NormalizeLadder();
        }

        private static Mesh[] FindLodMeshes(RendererBinding[] bindings, SkinnedMeshRenderer smr)
        {
            if (bindings == null)
            {
                return null;
            }
            for (int i = 0; i < bindings.Length; i++)
            {
                if (bindings[i].renderer == smr)
                {
                    return bindings[i].lodMeshes;
                }
            }
            return null;
        }

        /// <summary>アタッチ時の Bounds 計算(Editor アセンブリの Calculate from All Clips が登録する)。</summary>
        public static System.Action<AnimatorLod> EditorResetBoundsHook;

        /// <summary>現在の SMR の Bounds を合成した、このコンポーネントのローカル空間の AABB。</summary>
        public bool EditorComputeRendererBounds(out Bounds result)
        {
            result = default;
            if (renderers == null || renderers.Length == 0)
            {
                return false;
            }

            var toLocal = transform.worldToLocalMatrix;
            bool any = false;
            foreach (var binding in renderers)
            {
                var smr = binding.renderer;
                if (smr == null)
                {
                    continue;
                }
                var world = smr.bounds;
                var c = world.center;
                var e = world.extents;
                for (int i = 0; i < 8; i++)
                {
                    var corner = c + Vector3.Scale(e, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    var p = toLocal.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        result = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        result.Encapsulate(p);
                    }
                }
            }
            return any;
        }

        /// <summary>指定カメラでの LOD をランタイムと同じ式で求める(Culled は LodLevelCount)。</summary>
        public int EditorPreviewLod(Camera cam, out float ratio, out bool culled)
        {
            return EditorPreviewLod(cam, out ratio, out culled, out _);
        }

        /// <summary>指定カメラでの LOD と Near Culled をランタイムと同じ式で求める(Culled は LodLevelCount)。</summary>
        public int EditorPreviewLod(Camera cam, out float ratio, out bool culled, out bool nearCulled)
        {
            ratio = ComputeScreenRatio(transform.localToWorldMatrix, new LodView(cam, cam.transform), out float distance);
            culled = IsCulledRatio(ratio);
            nearCulled = IsNearCulledDistance(distance);
            return culled ? LodLevelCount : LevelForRatio(ratio);
        }

        /// <summary>LOD 判定に使う外接球(ワールド空間)。ランタイムと同じ式。</summary>
        public void EditorGetLodSphere(out Vector3 center, out float radius)
        {
            var localToWorld = transform.localToWorldMatrix;
            center = localToWorld.MultiplyPoint3x4(lodBounds.center);
            radius = LodSphereRadius(localToWorld);
        }

        /// <summary>Bounds の計算結果を書き込む。</summary>
        public void EditorSetCalculatedBounds(Bounds bounds, BoundsSource source)
        {
            lodBounds = bounds;
            boundsSource = source;
        }
#endif
    }
}
