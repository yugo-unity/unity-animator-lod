using System.Text;
using UnityEngine;

namespace AnimatorLodTest
{
    /// <summary>
    /// AnimatorLod 検証シーン用 HUD(画面左上、IMGUI 表示のみ)。
    /// FPS / フレーム時間 / 体数 / CullingMode に加え、LOD の状態と
    /// LOD 別の個体数を表示する。
    /// </summary>
    public sealed class LodPerformanceHud : MonoBehaviour
    {
        [SerializeField]
        private HumanoidSpawner spawner;
        [SerializeField]
        private LodControlPanel panel;
        [SerializeField]
        private CrowdMover mover;
        [SerializeField, Tooltip("計測精度のため vSync を切り、フレームレート上限を解除する")]
        private bool disableVSync = true;
        [SerializeField, Min(0.1f)]
        private float sampleWindow = 0.5f;

        private int _frameCount;
        private float _elapsed;
        private float _worstDelta;

        private float _fps;
        private float _avgMs;
        private float _worstMs;

        private GUIStyle _style;
        private readonly int[] _lodCounts = new int[16];
        private readonly StringBuilder _sb = new StringBuilder(256);
        private string _text = string.Empty;

        private void Awake()
        {
            if (disableVSync)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }

            if (spawner == null)
            {
                spawner = GetComponent<HumanoidSpawner>();
            }
            if (panel == null)
            {
                panel = GetComponent<LodControlPanel>();
            }
            if (mover == null)
            {
                mover = GetComponent<CrowdMover>();
            }
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _frameCount++;
            _elapsed += dt;
            if (dt > _worstDelta)
            {
                _worstDelta = dt;
            }

            if (_elapsed >= sampleWindow)
            {
                _fps = _frameCount / _elapsed;
                _avgMs = _elapsed / _frameCount * 1000f;
                _worstMs = _worstDelta * 1000f;

                _frameCount = 0;
                _elapsed = 0f;
                _worstDelta = 0f;

                // 表示文字列は sampleWindow ごとにだけ組み直す(毎フレーム / OnGUI 毎の GC Alloc を避ける)。
                // IMGUI は string しか受け取れないので、この周期の 1 回だけ ToString する。
                RebuildText();
            }
        }

        private void OnGUI()
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16,
                    wordWrap = false,
                    normal = { textColor = Color.white }
                };
            }

            GUI.Box(new Rect(8, 8, 640, 140), GUIContent.none);
            GUI.Label(new Rect(16, 12, 632, 134), _text, _style);
        }

        private void RebuildText()
        {
            int count = spawner != null ? spawner.SpawnedCount : 0;
            string culling = spawner != null ? HudText.CullingModeName(spawner.CullingMode) : "-";
            string rig = spawner != null && spawner.UseOptimizedRig ? "Optimized" : "Normal";
            string moving = HudText.OnOff(mover != null && mover.Move);
            string lod = panel != null ? HudText.OnOff(panel.LodEnabled) : "-";

            _sb.Clear();
            _sb.Append("FPS: ");
            HudText.AppendFixed(_sb, _fps, 1);
            _sb.Append('\n');
            _sb.Append("Frame: avg ");
            HudText.AppendFixed(_sb, _avgMs, 2);
            _sb.Append(" ms / worst ");
            HudText.AppendFixed(_sb, _worstMs, 2);
            _sb.Append(" ms\n");
            _sb.Append("Animators: ").Append(count).Append("  Rig: ").Append(rig).Append("  Move: ").Append(moving).Append('\n');
            _sb.Append("CullingMode: ").Append(culling).Append('\n');
            _sb.Append("Anim LOD: ").Append(lod);
            if (panel != null)
            {
                _sb.Append("  Base Interval: ").Append(panel.BaseInterval);
                _sb.Append("  Mesh LOD: ").Append(HudText.OnOff(panel.MeshLodEnabled));
                _sb.Append("  Skin Weights LOD: ").Append(HudText.OnOff(panel.SkinWeightsLodEnabled));
            }
            _sb.Append('\n');
            AppendLodCounts();

            _text = _sb.ToString();
        }

        private void AppendLodCounts()
        {
            var instances = AnimatorLodSystem.Instances;
            System.Array.Clear(_lodCounts, 0, _lodCounts.Length);
            int levels = 0;
            int off = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                var opt = instances[i];
                levels = Mathf.Max(levels, opt.LodLevelCount);
                int lod = opt.CurrentLod;
                if (lod < 0)
                {
                    off++;
                }
                else if (lod < _lodCounts.Length)
                {
                    _lodCounts[lod]++;
                }
            }

            if (levels == 0)
            {
                _sb.Append("LOD: (no AnimatorLod)");
                return;
            }

            _sb.Append("LOD: ");
            for (int i = 0; i < levels; i++)
            {
                _sb.Append('L').Append(i).Append('=').Append(_lodCounts[i]).Append(' ');
            }
            _sb.Append("Culled=").Append(_lodCounts[Mathf.Min(levels, _lodCounts.Length - 1)]);
            _sb.Append("  Invisible=").Append(AnimatorLodSystem.InvisibleCount);
            _sb.Append("  Enabled/frame=").Append(AnimatorLodSystem.EnabledThisFrame);
            if (off > 0)
            {
                _sb.Append("  (LOD off: ").Append(off).Append(')');
            }
        }
    }
}
