using System.Collections.Generic;
using UnityEngine;

namespace AnimatorStressTest
{
    /// <summary>
    /// SimpleHumanoid を正方グリッドに大量配置し、
    /// 各個体にランダムなステートをランダム位相で再生させる。
    /// 画面外でもアニメーションを止めないよう cullingMode は既定で AlwaysAnimate。
    /// </summary>
    public sealed class HumanoidSpawner : MonoBehaviour
    {
        [SerializeField]
        private GameObject sourcePrefab;
        [SerializeField]
        private GameObject optimizedPrefab; // Optimize Game Objects 版
        [SerializeField]
        private bool useOptimizedRig;
        [SerializeField]
        private RuntimeAnimatorController animatorController;
        [SerializeField]
        private string[] stateNames = { "Idle Walk Run Blend", "JumpStart", "InAir", "JumpLand" };

        [SerializeField, Min(1)]
        private int count = 1000;
        [SerializeField, Min(0.1f)]
        private float spacing = 2f;
        [SerializeField]
        private AnimatorCullingMode cullingMode = AnimatorCullingMode.AlwaysAnimate;
        [SerializeField]
        private int randomSeed = 12345;

        [Header("Locomotion Speed")]
        [SerializeField, Tooltip("Ping-pong the Speed parameter between Min and Max every frame so Walk and Run blend back and forth.")]
        private bool pingPongSpeed = true;
        [SerializeField, Tooltip("Speed range (x = Min, y = Max). The blend thresholds are Walk = 2, Run = 6.")]
        private Vector2 speedRange = new Vector2(2f, 6f);
        [SerializeField, Min(0.1f), Tooltip("Seconds for one Min -> Max -> Min cycle.")]
        private float pingPongPeriod = 8f;

        // 位相をずらして全個体が同時に歩き/走りにならないようにする(乱数列を変えないよう index から決める)
        private const float GoldenRatioFraction = 0.618034f;

        private readonly List<Animator> _spawned = new List<Animator>(1024);
        private Transform _container;

        public int Count => count;
        public int SpawnedCount => _spawned.Count;
        public AnimatorCullingMode CullingMode => cullingMode;
        public bool UseOptimizedRig => useOptimizedRig;
        public bool HasOptimizedPrefab => optimizedPrefab != null;
        public bool PingPongSpeed => pingPongSpeed;

        /// <summary>Speed の往復の ON/OFF。OFF にするとその時点の Speed のまま止まる。</summary>
        public void SetPingPongSpeed(bool enable)
        {
            pingPongSpeed = enable;
        }
        public IReadOnlyList<Animator> Spawned => _spawned;

        /// <summary>Respawn完了後に発火。最適化の再適用・キャッシュ再構築に使う。</summary>
        public event System.Action Respawned;

        private void Start()
        {
            Respawn();
        }

        /// <summary>
        /// Speed を speedRange の間で往復させる。Update 段で与えるので、AnimatorLodSystem(Update 直後)と
        /// Animator 評価の前に反映される。間引き中の Animator は次の評価フレームでその時点の値を使う。
        /// </summary>
        private void Update()
        {
            if (!pingPongSpeed || _hasLocomotionParams != true)
            {
                return;
            }

            float cycle = Time.time / pingPongPeriod;
            for (int i = 0; i < _spawned.Count; i++)
            {
                var animator = _spawned[i];
                if (animator == null)
                {
                    continue;
                }
                float t = Mathf.PingPong((cycle + i * GoldenRatioFraction) * 2f, 1f);
                animator.SetFloat(SpeedHash, Mathf.Lerp(speedRange.x, speedRange.y, t));
            }
        }

        /// <summary>体数を変更して再生成する。</summary>
        public void SetCount(int newCount)
        {
            count = Mathf.Max(1, newCount);
            Respawn();
        }

        /// <summary>生成済み全個体の cullingMode を即時変更する(再生成なし)。</summary>
        public void SetCullingMode(AnimatorCullingMode mode)
        {
            cullingMode = mode;
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                {
                    _spawned[i].cullingMode = mode;
                }
            }
        }

        /// <summary>Optimize Game Objects 版プレハブへの切替(再生成が発生)。</summary>
        public void SetUseOptimizedRig(bool enable)
        {
            if (enable && optimizedPrefab == null)
            {
                Debug.LogWarning("[HumanoidSpawner] optimizedPrefab が未設定です。", this);
                return;
            }

            useOptimizedRig = enable;
            Respawn();
        }

        public void Respawn()
        {
            Clear();
            _hasLocomotionParams = null;

            var prefab = useOptimizedRig && optimizedPrefab != null ? optimizedPrefab : sourcePrefab;
            if (prefab == null)
            {
                Debug.LogError("[HumanoidSpawner] sourcePrefab が未設定です。", this);
                return;
            }

            if (_container == null)
            {
                var go = new GameObject("Humanoids");
                go.transform.SetParent(transform, false);
                _container = go.transform;
            }

            var rng = new System.Random(randomSeed);
            int columns = Mathf.CeilToInt(Mathf.Sqrt(count));
            int rows = Mathf.CeilToInt(count / (float)columns);
            var origin = new Vector3(
                -(columns - 1) * spacing * 0.5f,
                0f,
                -(rows - 1) * spacing * 0.5f);

            for (int i = 0; i < count; i++)
            {
                int x = i % columns;
                int z = i / columns;
                var pos = origin + new Vector3(x * spacing, 0f, z * spacing);

                var instance = Instantiate(prefab, pos, Quaternion.identity, _container);
                instance.name = prefab.name + "_" + i;

                var animator = instance.GetComponent<Animator>();
                if (animator == null)
                {
                    animator = instance.AddComponent<Animator>();
                }

                if (animatorController != null)
                {
                    animator.runtimeAnimatorController = animatorController;
                }

                animator.cullingMode = cullingMode;
                animator.applyRootMotion = false;
                // GameObject非アクティブ化経路でも状態を保持(更新間引き用の保険)
                animator.keepAnimatorStateOnDisable = true;

                if (stateNames != null && stateNames.Length > 0)
                {
                    string state = stateNames[rng.Next(stateNames.Length)];
                    float normalizedTime = (float)rng.NextDouble();
                    animator.Play(state, 0, normalizedTime);
                }

                // StarterAssets コントローラ向け: "Idle Walk Run Blend" は MotionSpeed を
                // ステート速度倍率に使う(既定 0 で凍結する)ため 1 を与え、Speed を
                // Idle/Walk/Run の閾値(0/2/6)からランダムに選んで個体差を出す(pingPongSpeed 有効時は Update で上書き)。
                ApplyLocomotionParameters(animator, rng);

                _spawned.Add(animator);
            }

            Respawned?.Invoke();
        }

        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int MotionSpeedHash = Animator.StringToHash("MotionSpeed");
        private static readonly int GroundedHash = Animator.StringToHash("Grounded");
        //private static readonly float[] LocomotionSpeeds = { 0f, 2f, 6f };
        private static readonly float[] LocomotionSpeeds = { 2f, 6f }; // skipt "Idle" motion
        private bool? _hasLocomotionParams;

        private void ApplyLocomotionParameters(Animator animator, System.Random rng)
        {
            if (_hasLocomotionParams == null)
            {
                bool speed = false, motion = false, grounded = false;
                foreach (var p in animator.parameters)
                {
                    if (p.nameHash == SpeedHash) speed = true;
                    else if (p.nameHash == MotionSpeedHash) motion = true;
                    else if (p.nameHash == GroundedHash) grounded = true;
                }
                _hasLocomotionParams = speed && motion && grounded;
            }

            if (_hasLocomotionParams != true)
            {
                return;
            }

            animator.SetFloat(MotionSpeedHash, 1f);
            animator.SetBool(GroundedHash, true);
            animator.SetFloat(SpeedHash, LocomotionSpeeds[rng.Next(LocomotionSpeeds.Length)]);
        }

        public void Clear()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                {
                    Destroy(_spawned[i].gameObject);
                }
            }
            _spawned.Clear();
        }
    }
}
