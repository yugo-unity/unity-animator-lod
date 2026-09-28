using UnityEngine;

namespace AnimatorStressTest
{
    /// <summary>
    /// RootNode をアニメーション外のスクリプトで Transform 移動させる前提を再現する。
    /// 各個体がスポーン位置を中心とした円軌道を移動(Root Motionなし・決定論的)。
    /// </summary>
    public sealed class CrowdMover : MonoBehaviour
    {
        private const float GoldenAngle = 2.399963f;

        [SerializeField]
        private HumanoidSpawner spawner;
        [SerializeField]
        private bool move = true;
        [SerializeField, Min(0f)]
        private float radius = 1.25f;
        [SerializeField, Min(0f)]
        private float angularSpeed = 0.8f; // rad/s

        private Transform[] _roots;
        private Vector3[] _centers;
        private float[] _phases;

        public bool Move => move;

        private void Awake()
        {
            if (spawner == null)
            {
                spawner = GetComponent<HumanoidSpawner>();
            }
        }

        private void OnEnable()
        {
            if (spawner != null)
            {
                spawner.Respawned += RebuildCache;
            }
        }

        private void OnDisable()
        {
            if (spawner != null)
            {
                spawner.Respawned -= RebuildCache;
            }
        }

        public void SetMove(bool enable)
        {
            move = enable;
        }

        private void RebuildCache()
        {
            var animators = spawner.Spawned;
            int count = animators.Count;
            _roots = new Transform[count];
            _centers = new Vector3[count];
            _phases = new float[count];

            for (int i = 0; i < count; i++)
            {
                if (animators[i] == null)
                {
                    continue;
                }

                var root = animators[i].transform;
                _roots[i] = root;
                _centers[i] = root.position;
                _phases[i] = i * GoldenAngle;
            }
        }

        private void Update()
        {
            if (!move || _roots == null)
            {
                return;
            }

            float t = Time.time * angularSpeed;
            for (int i = 0; i < _roots.Length; i++)
            {
                var root = _roots[i];
                if (root == null)
                {
                    continue;
                }

                float angle = t + _phases[i];
                float sin = Mathf.Sin(angle);
                float cos = Mathf.Cos(angle);

                var position = _centers[i] + new Vector3(sin * radius, 0f, cos * radius);
                // 円軌道の接線方向を向く
                var forward = new Vector3(cos, 0f, -sin);
                root.SetPositionAndRotation(position, Quaternion.LookRotation(forward));
            }
        }
    }
}
