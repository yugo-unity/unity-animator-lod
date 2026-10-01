using UnityEngine;
using UnityEngine.UI;

namespace AnimatorLodTest
{
    /// <summary>
    /// AnimatorLod 検証シーン用の簡易 uGUI パネル(画面右上に動的生成)。
    /// 体数・CullingMode に加え、全個体の Animation LOD を一括トグルする。
    /// Respawn 後もトグル状態を再適用する。
    /// </summary>
    public sealed class LodControlPanel : MonoBehaviour
    {
        [SerializeField]
        private HumanoidSpawner spawner;
        [SerializeField]
        private CrowdMover mover;

        private static readonly AnimatorCullingMode[] CullingModes =
        {
            AnimatorCullingMode.AlwaysAnimate,
            AnimatorCullingMode.CullUpdateTransforms,
            AnimatorCullingMode.CullCompletely,
        };

        private Font _font;
        private Text _countLabel;
        private Text _cullingButtonLabel;
        private Text _lodButtonLabel;
        private Text _rigButtonLabel;
        private Text _moveButtonLabel;
        private Text _pingPongButtonLabel;
        private int _cullingIndex;
        private int _lastCount = -1;

        private Text _baseIntervalLabel;

        private Text _meshLodButtonLabel;
        private Text _skinWeightsButtonLabel;

        // null = プレハブ既定値のまま(まだ操作していない)
        private bool? _lodEnabled;
        private bool? _meshLod;
        private bool? _skinWeights;
        private int? _baseInterval;

        public bool LodEnabled => _lodEnabled ?? FirstOptimizer()?.LodEnabled ?? false;
        public bool MeshLodEnabled => _meshLod ?? FirstOptimizer()?.MeshLodEnabled ?? false;
        public bool SkinWeightsLodEnabled => _skinWeights ?? FirstOptimizer()?.SkinWeightsLodEnabled ?? false;
        public int BaseInterval => _baseInterval ?? FirstOptimizer()?.BaseInterval ?? 0;

        /// <summary>全個体の Base Interval を変更する(外部/ボタン共用)。</summary>
        public void SetBaseInterval(int value)
        {
            _baseInterval = Mathf.Clamp(value, 0, AnimatorLod.MaxInterval);
            ApplyBaseInterval(_baseInterval.Value);
            RefreshLabels();
        }

        private void Awake()
        {
            if (spawner == null)
            {
                spawner = GetComponent<HumanoidSpawner>();
            }
            if (mover == null)
            {
                mover = GetComponent<CrowdMover>();
            }

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            if (spawner != null)
            {
                int index = System.Array.IndexOf(CullingModes, spawner.CullingMode);
                _cullingIndex = Mathf.Max(0, index);
            }

            ControlPanelUi.EnsureEventSystem();
            BuildUi();
        }

        private void OnEnable()
        {
            if (spawner != null)
            {
                spawner.Respawned += OnRespawned;
            }
        }

        private void OnDisable()
        {
            if (spawner != null)
            {
                spawner.Respawned -= OnRespawned;
            }
        }

        private void LateUpdate()
        {
            // 体数は Respawn 時にしか変わらないので、変化したフレームだけ文字列を作る(毎フレームの GC Alloc 回避)
            if (_countLabel != null && spawner != null && spawner.SpawnedCount != _lastCount)
            {
                _lastCount = spawner.SpawnedCount;
                _countLabel.text = "Count: " + _lastCount;
            }
        }

        private void OnRespawned()
        {
            if (_lodEnabled.HasValue)
            {
                ApplyLod(_lodEnabled.Value);
            }
            if (_meshLod.HasValue)
            {
                ApplyMeshLod(_meshLod.Value);
            }
            if (_skinWeights.HasValue)
            {
                ApplySkinWeights(_skinWeights.Value);
            }
            if (_baseInterval.HasValue)
            {
                ApplyBaseInterval(_baseInterval.Value);
            }
            RefreshLabels();
        }

        private void ApplyBaseInterval(int value)
        {
            var list = spawner.Spawned;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].TryGetComponent<AnimatorLod>(out var opt))
                {
                    opt.BaseInterval = value;
                }
            }
        }

        private AnimatorLod FirstOptimizer()
        {
            if (spawner == null)
            {
                return null;
            }
            var list = spawner.Spawned;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].TryGetComponent<AnimatorLod>(out var opt))
                {
                    return opt;
                }
            }
            return null;
        }

        private void ApplyLod(bool enable)
        {
            var list = spawner.Spawned;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].TryGetComponent<AnimatorLod>(out var opt))
                {
                    opt.SetLodEnabled(enable);
                }
            }
        }

        private void ApplyMeshLod(bool enable)
        {
            var list = spawner.Spawned;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].TryGetComponent<AnimatorLod>(out var opt))
                {
                    opt.SetMeshLodEnabled(enable);
                }
            }
        }

        private void ApplySkinWeights(bool enable)
        {
            var list = spawner.Spawned;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].TryGetComponent<AnimatorLod>(out var opt))
                {
                    opt.SetSkinWeightsLodEnabled(enable);
                }
            }
        }

        private void BuildUi()
        {
            var panel = ControlPanelUi.CreatePanel(transform);

            // ---- 共通: 体数 / CullingMode / Respawn ----
            _countLabel = ControlPanelUi.CreateHeader(panel, _font, "Count: -");
            ControlPanelUi.CreateButton(panel, _font, "+100", () => AddCount(100));
            ControlPanelUi.CreateButton(panel, _font, "+500", () => AddCount(500));
            ControlPanelUi.CreateButton(panel, _font, "-100", () => AddCount(-100));
            ControlPanelUi.CreateButton(panel, _font, "-500", () => AddCount(-500));
            _cullingButtonLabel = ControlPanelUi.CreateButton(panel, _font, CullingLabel(), CycleCullingMode);
            ControlPanelUi.CreateButton(panel, _font, "Respawn", () =>
            {
                if (spawner != null)
                {
                    spawner.Respawn();
                }
            });

            // ---- シーン固有: AnimatorLod ----
            ControlPanelUi.CreateSpacer(panel);
            _lodButtonLabel = ControlPanelUi.CreateButton(panel, _font, LodLabel(), ToggleLod);
            _baseIntervalLabel = ControlPanelUi.CreateLabelBox(panel, _font, BaseIntervalLabel());
            ControlPanelUi.CreateButton(panel, _font, "Base Interval -", () => SetBaseInterval(BaseInterval - 1));
            ControlPanelUi.CreateButton(panel, _font, "Base Interval +", () => SetBaseInterval(BaseInterval + 1));
            _meshLodButtonLabel = ControlPanelUi.CreateButton(panel, _font, MeshLodLabel(), ToggleMeshLod);
            _skinWeightsButtonLabel = ControlPanelUi.CreateButton(panel, _font, SkinWeightsLabel(), ToggleSkinWeights);

            // ---- 共通: Rig / Move ----
            ControlPanelUi.CreateSpacer(panel);
            _rigButtonLabel = ControlPanelUi.CreateButton(panel, _font, RigLabel(), ToggleRig);
            _moveButtonLabel = ControlPanelUi.CreateButton(panel, _font, MoveLabel(), ToggleMove);
            _pingPongButtonLabel = ControlPanelUi.CreateButton(panel, _font, PingPongLabel(), TogglePingPong);
        }

        private void ToggleLod()
        {
            if (spawner == null)
            {
                return;
            }
            _lodEnabled = !LodEnabled;
            ApplyLod(_lodEnabled.Value);
            RefreshLabels();
        }

        private void ToggleMeshLod()
        {
            if (spawner == null)
            {
                return;
            }
            _meshLod = !MeshLodEnabled;
            ApplyMeshLod(_meshLod.Value);
            RefreshLabels();
        }

        private void ToggleSkinWeights()
        {
            if (spawner == null)
            {
                return;
            }
            _skinWeights = !SkinWeightsLodEnabled;
            ApplySkinWeights(_skinWeights.Value);
            RefreshLabels();
        }

        private void ToggleRig()
        {
            if (spawner == null || !spawner.HasOptimizedPrefab)
            {
                return;
            }

            spawner.SetUseOptimizedRig(!spawner.UseOptimizedRig);
            RefreshLabels();
        }

        private void ToggleMove()
        {
            if (mover == null)
            {
                return;
            }

            mover.SetMove(!mover.Move);
            RefreshLabels();
        }

        private void TogglePingPong()
        {
            if (spawner == null)
            {
                return;
            }

            spawner.SetPingPongSpeed(!spawner.PingPongSpeed);
            RefreshLabels();
        }

        private void RefreshLabels()
        {
            if (_lodButtonLabel != null) _lodButtonLabel.text = LodLabel();
            if (_meshLodButtonLabel != null) _meshLodButtonLabel.text = MeshLodLabel();
            if (_skinWeightsButtonLabel != null) _skinWeightsButtonLabel.text = SkinWeightsLabel();
            if (_baseIntervalLabel != null) _baseIntervalLabel.text = BaseIntervalLabel();
            if (_rigButtonLabel != null) _rigButtonLabel.text = RigLabel();
            if (_moveButtonLabel != null) _moveButtonLabel.text = MoveLabel();
            if (_pingPongButtonLabel != null) _pingPongButtonLabel.text = PingPongLabel();
            if (_cullingButtonLabel != null) _cullingButtonLabel.text = CullingLabel();
        }

        private string LodLabel() => "Anim LOD: " + OnOff(LodEnabled);
        private string MeshLodLabel() => "Mesh LOD: " + OnOff(MeshLodEnabled);
        private string SkinWeightsLabel() => "Skin Weights LOD: " + OnOff(SkinWeightsLodEnabled);
        private string BaseIntervalLabel() => "Base Interval: " + BaseInterval;
        private string RigLabel() => "Optimized Rig: " + OnOff(spawner != null && spawner.UseOptimizedRig);
        private string MoveLabel() => "Move: " + OnOff(mover != null && mover.Move);
        private string PingPongLabel() => "Ping Pong Speed: " + OnOff(spawner != null && spawner.PingPongSpeed);
        private string CullingLabel() => "Culling: " + CullingModes[_cullingIndex];

        private static string OnOff(bool value) => ControlPanelUi.OnOff(value);

        private void AddCount(int delta)
        {
            if (spawner != null)
            {
                spawner.SetCount(spawner.Count + delta);
            }
        }

        private void CycleCullingMode()
        {
            if (spawner == null)
            {
                return;
            }

            _cullingIndex = (_cullingIndex + 1) % CullingModes.Length;
            spawner.SetCullingMode(CullingModes[_cullingIndex]);
            RefreshLabels();
        }
    }
}
