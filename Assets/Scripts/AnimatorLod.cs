using UnityEngine;

namespace AnimatorStressTest
{
    /// <summary>
    /// Animator を持つ GameObject に 1 つずつ付ける個体単位の最適化コンポーネント
    /// (Static Bounds / Animation LOD / Mesh LOD / Skin Weights LOD)。毎フレームの判定と切替は <see cref="AnimatorLodSystem"/> が行う。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator))]
    public sealed class AnimatorLod : MonoBehaviour
    {
        public const int MaxInterval = 4;
        public const float MinRatio = 0.0001f;

        // ---- References(未設定なら Awake で取得) ----
        [SerializeField, Tooltip("Target Animator. Empty = Animator on this GameObject.")]
        private Animator animator;

        /// <summary>SMR ごとの事前準備データ。元の値は各機能を無効にしたときの復元先。</summary>
        [System.Serializable]
        private struct RendererBinding
        {
            public SkinnedMeshRenderer renderer;
            [Tooltip("Element i is the mesh for LOD i. Empty = original mesh.")]
            public Mesh[] lodMeshes;
            public Transform rootBone;
            public Bounds localBounds;
            public Mesh mesh;
            public SkinQuality quality;
            [Tooltip("Bounds in the renderer's local space. Assigned as localBounds when Static Bounds is on.")]
            public Bounds staticLocalBounds;
        }

        [SerializeField, Tooltip("Target renderers and their prepared data. Empty = all children, fetched at Awake.")]
        private RendererBinding[] renderers;

        // ---- Static Bounds ----
        [SerializeField]
        private bool staticBounds;
        [SerializeField, Tooltip("AABB in this transform's local space. Used for LOD, and as fixed bounds when Static Bounds is on.")]
        private Bounds lodBounds = new Bounds(new Vector3(0f, 1f, 0f), new Vector3(2.5f, 2.5f, 2.5f));
        [SerializeField, HideInInspector]
        private bool boundsCalculated;

        // ---- Animation LOD ----
        [SerializeField]
        private bool lodEnabled = true;
        [SerializeField, Tooltip("Camera for the screen-size ratio. Empty = Camera.main.")]
        private Camera lodCamera;
        // 配列フィールドは初期化子を持たない(Instantiate のたびにコンストラクタで確保され、デシリアライズで上書きされて捨てられるため)。
        // 既定値は Reset(Editor)と、Reset を経ないランタイムの AddComponent 用に Awake で与える。
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
        private bool _staticBoundsApplied;
        private int _currentLod = -1;
        private float _screenRatio;
        private bool _culled;
        private bool _immediateRequested;
        private float _speed = 1f;

        public bool StaticBounds => staticBounds;
        public bool LodEnabled => lodEnabled;
        public bool MeshLodEnabled => meshLodEnabled;
        public bool SkinWeightsLodEnabled => skinWeightsLodEnabled;
        public bool BoundsCalculated => boundsCalculated;
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
        /// <summary>直近の判定で不可視だったか。</summary>
        public bool IsInvisible { get; internal set; }
        /// <summary>直近の判定で適用した更新間隔。</summary>
        public int CurrentInterval { get; internal set; }
        /// <summary>割り当て中の位相バケット。</summary>
        public int CurrentBucket => Schedule.Bucket;

        // ---- AnimatorLodSystem 用の内部状態 ----
        internal Animator Animator => animator;
        internal AnimatorLodSystem.InstanceSchedule Schedule = AnimatorLodSystem.InstanceSchedule.Unregistered;
        /// <summary>ResetRuntimeState で戻すべき状態があるか。</summary>
        internal bool HasLodState => _currentLod >= 0 || _appliedMeshLod != 0 || _appliedSkinLod != 0;

        // Awake 以降のランタイム処理は次を前提とし、null チェックしない:
        //   animator / renderers / lod* 配列は非 null、lodMeshes は非 null(要素は null 可 = 元の Mesh)。
        //   Animator が破棄されるのは AnimatorLod と同時のみ(個別の破棄は想定しない)。
        //   SMR は個別に破棄されうる(装備の付け替え等)。renderers を走査する処理は先頭で RemoveMissingRenderers を呼び、破棄済みを除く。
        private void Awake()
        {
            _transform = transform;
            _lodCameraTransform = lodCamera != null ? lodCamera.transform : null;
            if (lodRatios == null || lodRatios.Length == 0)
            {
                // Reset を経ずに追加された場合(ランタイムの AddComponent)
                ApplyLadderDefaults();
            }
            PrepareReferences();
            animator.keepAnimatorStateOnDisable = true;
        }

        private void ApplyLadderDefaults()
        {
            lodRatios = (float[])DefaultLodRatios.Clone();
            lodIntervals = (int[])DefaultLodIntervals.Clone();
            lodSkinQualities = (SkinQuality[])DefaultLodSkinQualities.Clone();
        }

        /// <summary>References が未設定なら警告付きで取得し、破棄済みの SMR を除く。</summary>
        private void PrepareReferences()
        {
            if (animator == null)
            {
                Debug.LogWarning($"[AnimatorLod] '{name}': Animator is not assigned in References. " +
                                 "Fetching it at Awake. Use 'Refresh References' in the Inspector to assign it beforehand.", this);
                animator = GetComponent<Animator>();
            }

            if (renderers == null || renderers.Length == 0)
            {
                Debug.LogWarning($"[AnimatorLod] '{name}': Skinned Mesh Renderers are not assigned in References. " +
                                 "Fetching them at Awake. Use 'Refresh References' in the Inspector to assign them beforehand.", this);
                var smrs = GetComponentsInChildren<SkinnedMeshRenderer>(true);
                renderers = new RendererBinding[smrs.Length];
                for (int i = 0; i < smrs.Length; i++)
                {
                    renderers[i] = CreateBinding(smrs[i], System.Array.Empty<Mesh>());
                }
                return;
            }

            RemoveMissingRenderers();
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
            binding.rootBone = smr.rootBone;
            binding.localBounds = smr.localBounds;
            binding.mesh = smr.sharedMesh;
            binding.quality = smr.quality;
            binding.staticLocalBounds = ComputeStaticLocalBounds(smr);
        }

        /// <summary>lodBounds を SMR 空間へ変換する(SMR とルートの相対 Transform は不変とみなす)。</summary>
        private Bounds ComputeStaticLocalBounds(SkinnedMeshRenderer smr)
        {
            return TransformBounds(lodBounds, RootToRendererMatrix(smr.transform));
        }

        /// <summary>ルート空間 → SMR 空間の行列。ルートの配置に依存しないよう、ローカル Transform の連鎖から求める。</summary>
        private Matrix4x4 RootToRendererMatrix(Transform smrTransform)
        {
            var root = transform;
            // SMR 空間 → ルート空間 = 親側から順に掛けた各ローカル TRS の積
            var rendererToRoot = Matrix4x4.identity;
            for (var t = smrTransform; t != root; t = t.parent)
            {
                if (t == null)
                {
                    // ルートの子孫でない(通常は起きない)。連鎖が作れないのでワールド行列から求める
                    return smrTransform.worldToLocalMatrix * root.localToWorldMatrix;
                }
                rendererToRoot = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) * rendererToRoot;
            }
            return rendererToRoot.inverse;
        }

        private void OnEnable()
        {
            if (skinWeightsLodEnabled)
            {
                WarmSkinWeightCaches();
            }
            // 登録すると Animator.speed はシステムが書き換えるので、その前にユーザーの速度として取り込む
            _speed = animator.speed;
            AnimatorLodSystem.Register(this);
            ApplyStaticBounds(staticBounds);
        }

        private void OnDisable()
        {
            AnimatorLodSystem.Unregister(this);
            ResetRuntimeState();
            ApplyStaticBounds(false);
            if (!animator.enabled)
            {
                animator.enabled = true;
            }
            animator.speed = _speed;
        }

        // ---- 公開 API ----

        public void SetStaticBounds(bool enable)
        {
            staticBounds = enable;
            if (isActiveAndEnabled)
            {
                ApplyStaticBounds(enable);
            }
        }

        public void SetLodEnabled(bool enable)
        {
            lodEnabled = enable;
        }

        /// <summary>Mesh LOD の有効/無効。無効化時は元の Mesh に戻す。</summary>
        public void SetMeshLodEnabled(bool enable)
        {
            meshLodEnabled = enable;
            // 無効な間は SMR に触れない(OnDisable で元に戻し済みで、次に有効になった後の Tick で反映される)
            if (!enable && isActiveAndEnabled)
            {
                ApplyMeshLod(0);
            }
        }

        /// <summary>Skin Weights LOD の有効/無効。無効化時は元の quality に戻す。</summary>
        public void SetSkinWeightsLodEnabled(bool enable)
        {
            skinWeightsLodEnabled = enable;
            // 無効な間は SMR に触れない(OnDisable で元に戻し済みで、キャッシュは次の OnEnable で温める)
            if (!isActiveAndEnabled)
            {
                return;
            }
            if (enable)
            {
                WarmSkinWeightCaches();
            }
            else
            {
                ApplySkinQuality(0);
            }
        }

        /// <summary>次の Tick で間引きに関わらず 1 回評価させる。外部から Animator のステートやパラメータを変えた直後に呼ぶ。</summary>
        public void RequestImmediateUpdate()
        {
            _immediateRequested = true;
        }

        // ---- Static Bounds ----

        private void ApplyStaticBounds(bool enable)
        {
            if (enable == _staticBoundsApplied)
            {
                return;
            }

            RemoveMissingRenderers();
            for (int i = 0; i < renderers.Length; i++)
            {
                var binding = renderers[i];
                if (enable)
                {
                    binding.renderer.rootBone = null;
                    binding.renderer.localBounds = binding.staticLocalBounds;
                }
                else
                {
                    binding.renderer.rootBone = binding.rootBone;
                    binding.renderer.localBounds = binding.localBounds;
                }
            }

            _staticBoundsApplied = enable;
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
                Mesh mesh = null;
                if (level > 0 && level < binding.lodMeshes.Length)
                {
                    mesh = binding.lodMeshes[level];
                }
                if (mesh == null)
                {
                    mesh = binding.mesh;
                }

                // sharedMesh を差し替えても localBounds(固定 Bounds を含む)はエンジン側で保持される
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

            var quality = level > 0 && level < lodSkinQualities.Length
                ? lodSkinQualities[level]
                : SkinQuality.Auto;

            RemoveMissingRenderers();
            for (int i = 0; i < renderers.Length; i++)
            {
                var binding = renderers[i];
                var target = quality == SkinQuality.Auto ? binding.quality : quality;
                if (binding.renderer.quality != target)
                {
                    binding.renderer.quality = target;
                }
            }
        }

        private void RestoreOriginalMeshAndQuality()
        {
            ApplyMeshLod(0);
            ApplySkinQuality(0);
        }

        /// <summary>使用する Mesh と Skin Weights の組み合わせについて、キャッシュを事前に作る。</summary>
        private void WarmSkinWeightCaches()
        {
            int mask = GetSkinWeightsMask();
            if (mask == 0)
            {
                return;
            }

            RemoveMissingRenderers();
            for (int i = 0; i < renderers.Length; i++)
            {
                var binding = renderers[i];
                if (IsSkinWeightsWarmed(binding, mask))
                {
                    continue;
                }

                var smr = binding.renderer;
                var currentMesh = smr.sharedMesh;
                var currentQuality = smr.quality;

                AnimatorLodSystem.WarmSkinWeights(smr, binding.mesh, mask);
                for (int l = 0; l < binding.lodMeshes.Length; l++)
                {
                    AnimatorLodSystem.WarmSkinWeights(smr, binding.lodMeshes[l], mask);
                }

                if (smr.sharedMesh != currentMesh)
                {
                    smr.sharedMesh = currentMesh;
                }
                smr.quality = currentQuality;
            }
        }

        /// <summary>lodSkinQualities で使う Skin Weights のビットマスク(1 &lt;&lt; SkinQuality)。</summary>
        private int GetSkinWeightsMask()
        {
            int mask = 0;
            for (int i = 0; i < lodSkinQualities.Length; i++)
            {
                if (lodSkinQualities[i] != SkinQuality.Auto)
                {
                    mask |= 1 << (int)lodSkinQualities[i];
                }
            }
            return mask;
        }

        private static bool IsSkinWeightsWarmed(in RendererBinding binding, int mask)
        {
            if (!AnimatorLodSystem.IsSkinWeightsWarmed(binding.mesh, mask))
            {
                return false;
            }
            for (int l = 0; l < binding.lodMeshes.Length; l++)
            {
                if (!AnimatorLodSystem.IsSkinWeightsWarmed(binding.lodMeshes[l], mask))
                {
                    return false;
                }
            }
            return true;
        }

        private static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            var center = m.MultiplyPoint3x4(b.center);
            var e = b.extents;
            // 各軸の絶対値をとった行列で extents を変換(AABB の保守的な変換)
            var ext = new Vector3(
                Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
            return new Bounds(center, ext * 2f);
        }

        // ---- AnimatorLodSystem から呼ばれる判定 ----

        /// <summary>Screen Size 比から LOD と Culled を決め、Mesh LOD と Skin Weights LOD に反映する。</summary>
        internal void EvaluateLod(in LodView view)
        {
            float ratio = ComputeScreenRatio(_transform, view);
            _screenRatio = ratio;
            _culled = IsCulledRatio(ratio);
            int level = LevelForRatio(ratio);
            _currentLod = level;

            ApplyLodOverrides(_culled ? lodRatios.Length : level);
        }

        /// <summary>外接球が画面高さに占める割合。カメラが無ければ 1。</summary>
        private float ComputeScreenRatio(Transform t, in LodView view)
        {
            if (!view.HasCamera)
            {
                return 1f;
            }
            var s = t.lossyScale;
            // 3 引数の Mathf.Max は Max(params float[]) に解決され float[3] を毎回確保するため、2 引数版を入れ子にする
            float scale = Mathf.Max(Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y)), Mathf.Abs(s.z));
            float radius = lodBounds.extents.magnitude * scale;
            float denom = view.Orthographic
                ? view.Scale
                : Vector3.Distance(view.Position, t.TransformPoint(lodBounds.center)) * view.Scale;
            return radius / Mathf.Max(denom, 1e-4f);
        }

        private bool IsCulledRatio(float ratio) => cullRatio > 0f && ratio < cullRatio;

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

        /// <summary>LOD 判定の結果を捨て、Mesh と Skin Weights を元に戻す。</summary>
        internal void ResetRuntimeState()
        {
            _currentLod = -1;
            _culled = false;
            IsInvisible = false;
            CurrentInterval = 0;
            RestoreOriginalMeshAndQuality();
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
            // 編集中は SMR が元の状態なので、元の値と Static Bounds 用の Bounds を取り直して常に最新に保つ
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

            // 元の値は SMR が書き換わっているので取り直さず、lodBounds 由来の Static Bounds 用 Bounds だけを作り直す
            RemoveMissingRenderers();
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].staticLocalBounds = ComputeStaticLocalBounds(renderers[i].renderer);
            }
            // 一度戻してから掛け直し、Static Bounds の On/Off と作り直した Bounds の両方を反映する
            ApplyStaticBounds(false);
            ApplyStaticBounds(staticBounds);

            if (skinWeightsLodEnabled)
            {
                WarmSkinWeightCaches();
            }
            // 未適用に戻し、次の判定でテーブルを引き直させる(LOD 無効なら次の Tick で元の Mesh / quality に戻る)
            _appliedMeshLod = -1;
            _appliedSkinLod = -1;
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

        /// <summary>全 Renderer の元の値と Static Bounds 用の Bounds を取り直す。</summary>
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
                EditorSetCalculatedBounds(current, false);
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

        /// <summary>アタッチ時の Bounds 計算(Editor アセンブリの Calculate Bounding が登録する)。</summary>
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
            ratio = ComputeScreenRatio(transform, new LodView(cam, cam.transform));
            culled = IsCulledRatio(ratio);
            return culled ? LodLevelCount : LevelForRatio(ratio);
        }

        /// <summary>Calculate Bounding の結果を書き込む。</summary>
        public void EditorSetCalculatedBounds(Bounds bounds, bool fromClips)
        {
            lodBounds = bounds;
            boundsCalculated = fromClips;
            // Static Bounds 用の SMR 空間の Bounds は lodBounds から作るため取り直す
            EditorRefreshRendererState();
        }
#endif
    }
}
