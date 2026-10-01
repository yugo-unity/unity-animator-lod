using UnityEngine;

namespace AnimatorLodTest
{
    /// <summary>
    /// Dither Fade 確認用シーンのカメラ。キャラクターの列に沿って遠方(Culled の外)から列を突き抜けて往復し、
    /// Far(Culled)と Near(Near Cull Distance)のフェードを交互に起こす。
    /// 画面左上に各個体の距離・Screen Size 比・フェード状態を表示し、一時停止と位置の手動操作ができる(IMGUI のみで、入力システムに依存しない)。
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class DitherFadeDemoCamera : MonoBehaviour
    {
        [SerializeField, Tooltip("Z position at the far end (instances below Culled).")]
        private float farZ = -70f;
        [SerializeField, Tooltip("Z position at the near end (past the last instance of the row).")]
        private float nearZ = 18f;
        [SerializeField, Tooltip("Camera height.")]
        private float height = 1.1f;
        [SerializeField, Min(1f), Tooltip("Seconds for one round trip.")]
        private float period = 24f;
        [SerializeField]
        private bool autoMove = true;

        private Transform _transform;
        // 往復の位置(0 = farZ、1 = nearZ)
        private float _position;
        private float _phase;

        private void Awake()
        {
            _transform = transform;
        }

        private void Start()
        {
            // StarterAssets の Controller は既定のパラメータ(MotionSpeed = 0、Grounded = false)だと止まる / 落下へ遷移するので、接地して待機させる
            foreach (var lod in AnimatorLodSystem.Instances)
            {
                var animator = lod.GetComponent<Animator>();
                animator.SetBool("Grounded", true);
                animator.SetFloat("MotionSpeed", 1f);
                lod.RequestImmediateUpdate();
            }
            Apply();
        }

        private void Update()
        {
            if (autoMove)
            {
                // 両端で減速する往復(0 → 1 → 0 で 1 周期)
                _phase = Mathf.Repeat(_phase + Time.deltaTime / period, 1f);
                _position = 0.5f - 0.5f * Mathf.Cos(_phase * 2f * Mathf.PI);
            }
            Apply();
        }

        private void Apply()
        {
            _transform.SetPositionAndRotation(new Vector3(0f, height, Mathf.Lerp(farZ, nearZ, _position)), Quaternion.identity);
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10f, 10f, 520f, Screen.height - 20f), GUI.skin.box);
            GUILayout.Label($"Camera z = {_transform.position.z:0.0}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(autoMove ? "Pause" : "Resume", GUILayout.Width(80f)))
            {
                autoMove = !autoMove;
                // 再開時は今の位置から近い側へ進む向きで続ける
                _phase = Mathf.Acos(Mathf.Clamp(1f - 2f * _position, -1f, 1f)) / (2f * Mathf.PI);
            }
            GUILayout.Label("Far", GUILayout.Width(28f));
            float manual = GUILayout.HorizontalSlider(_position, 0f, 1f, GUILayout.Width(300f));
            GUILayout.Label("Near");
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(manual, _position))
            {
                autoMove = false;
                _position = manual;
            }

            var instances = AnimatorLodSystem.Instances;
            for (int i = 0; i < instances.Count; i++)
            {
                var lod = instances[i];
                // Near Cull Distance と同じく外接球の中心までの距離
                var center = lod.transform.TransformPoint(lod.LodBounds.center);
                float distance = Vector3.Distance(_transform.position, center);
                string state = !(lod.DitherFadeEnabled && AnimatorLod.DitherFadeSupported) ? "dither off"
                    : lod.IsFadeHidden ? "hidden"
                    : lod.IsFading ? $"fading {lod.FadeVisibility * 100f:0}%"
                    : "visible";
                string reason = lod.IsNearCulled ? " [near]" : lod.IsCulled ? " [culled]" : string.Empty;
                GUILayout.Label($"{lod.name}: {distance:0.0} m  {lod.CurrentScreenRatio * 100f:0.0}%  LOD {lod.CurrentLod}  {state}{reason}");
            }
            if (!AnimatorLod.DitherFadeSupported)
            {
                GUILayout.Label("LOD Cross Fade is off in the URP Asset: Dither Fade is disabled.");
            }
            GUILayout.EndArea();
        }
    }
}
