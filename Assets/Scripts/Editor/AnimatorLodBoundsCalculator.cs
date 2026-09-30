using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimatorLodTest.Editor
{
    /// <summary>
    /// SkinnedMeshRenderer をベイクした頂点を包含する AABB(ルート Transform ローカル空間)を求める。
    /// All Clips: コントローラの全クリップを全フレームサンプリングする。
    /// Default Pose: アニメーションを適用しない、プレハブ(シーン上の個体)のままのポーズをベイクする。
    /// プレビューシーン上の一時複製で行うため、対象がプレハブアセットでもシーン上でも元を汚さない。
    /// </summary>
    public static class AnimatorLodBoundsCalculator
    {
        private const int MaxSamplesPerClip = 600;

        /// <summary>コンポーネントのアタッチ時(AnimatorLod.Reset)も Calculate from All Clips と同じ計算にする。</summary>
        [InitializeOnLoadMethod]
        private static void RegisterResetHook()
        {
            AnimatorLod.EditorResetBoundsHook = lod => Apply(lod, AnimatorLod.BoundsSource.AllClips, false);
        }

        /// <summary>
        /// Inspector のボタンとアタッチ時で共通の本体。計算して AnimatorLod の Bounds に書き込む。
        /// mode は AllClips か DefaultPose。recordUndo はボタン操作時のみ(アタッチ時は Add Component の Undo に含まれる)。
        /// </summary>
        public static bool Apply(AnimatorLod lod, AnimatorLod.BoundsSource mode, bool recordUndo)
        {
            Bounds bounds;
            int samples = 1;
            AnimatorLod.BoundsSource source;
            bool ok = mode == AnimatorLod.BoundsSource.DefaultPose
                ? CalculateDefaultPose(lod, out bounds, out source)
                : Calculate(lod, out bounds, out samples, out source);
            if (!ok)
            {
                return false;
            }
            if (recordUndo)
            {
                Undo.RecordObject(lod, mode == AnimatorLod.BoundsSource.DefaultPose
                    ? "Calculate Bounds from Default Pose"
                    : "Calculate Bounds from All Clips");
            }
            lod.EditorSetCalculatedBounds(bounds, source);
            EditorUtility.SetDirty(lod);
            PrefabUtility.RecordPrefabInstancePropertyModifications(lod);
            switch (source)
            {
                case AnimatorLod.BoundsSource.AllClips:
                    Debug.Log($"[AnimatorLod] {lod.name}: bounds from all clips center={bounds.center} size={bounds.size} ({samples} samples)", lod);
                    break;
                case AnimatorLod.BoundsSource.DefaultPose:
                    Debug.Log($"[AnimatorLod] {lod.name}: bounds from default pose center={bounds.center} size={bounds.size}", lod);
                    break;
                default:
                    Debug.Log($"[AnimatorLod] {lod.name}: no Animator Controller, bounds taken from the current renderers. center={bounds.center} size={bounds.size}", lod);
                    break;
            }
            return true;
        }

        /// <summary>
        /// コントローラの全クリップから実測する(source = AllClips)。
        /// Animator に Controller が無い場合はエラーにせず、現在の Renderer の Bounds を返す(source = Renderers)。
        /// </summary>
        public static bool Calculate(AnimatorLod target, out Bounds bounds, out int sampleCount, out AnimatorLod.BoundsSource source)
        {
            bounds = default;
            sampleCount = 0;
            source = AnimatorLod.BoundsSource.Renderers;

            var sourceAnimator = target.GetComponent<Animator>();
            var controller = sourceAnimator != null ? sourceAnimator.runtimeAnimatorController : null;
            if (controller == null)
            {
                return target.EditorComputeRendererBounds(out bounds);
            }
            source = AnimatorLod.BoundsSource.AllClips;

            if (!sourceAnimator.hasTransformHierarchy)
            {
                // Optimize Game Objects 済みリグは AnimationMode でサンプリングできない
                // ("Optimized Game Object are not supported for sampling")。
                Debug.LogError("[AnimatorLod] Optimize Game Objects が有効なリグは実測できません。" +
                               "最適化前の同一スケルトンで Calculate from All Clips を行い、その Bounds を手動でコピーしてください。", target);
                return false;
            }

            var clips = new HashSet<AnimationClip>(controller.animationClips);
            clips.Remove(null);
            if (clips.Count == 0)
            {
                Debug.LogError("[AnimatorLod] Controller にクリップがありません。", target);
                return false;
            }

            int samples = 0;
            bool ok = MeasureOnPreviewCopy(target, (copy, accumulator) =>
            {
                AnimationMode.StartAnimationMode();
                try
                {
                    foreach (var clip in clips)
                    {
                        float frameRate = clip.frameRate > 0f ? clip.frameRate : 30f;
                        int steps = Mathf.Clamp(Mathf.CeilToInt(clip.length * frameRate), 1, MaxSamplesPerClip);
                        for (int s = 0; s <= steps; s++)
                        {
                            float t = clip.length * s / steps;
                            AnimationMode.SampleAnimationClip(copy, clip, t);
                            samples++;
                            accumulator.AddCurrentPose();
                        }
                    }
                }
                finally
                {
                    AnimationMode.StopAnimationMode();
                }
            }, out bounds);
            sampleCount = samples;
            return ok;
        }

        /// <summary>
        /// アニメーションを適用しないデフォルトポーズ(プレハブ / シーン上の個体に保存されたボーンの姿勢)から実測する。
        /// Controller の有無や Optimize Game Objects に関係なく使える。
        /// </summary>
        public static bool CalculateDefaultPose(AnimatorLod target, out Bounds bounds, out AnimatorLod.BoundsSource source)
        {
            source = AnimatorLod.BoundsSource.DefaultPose;
            return MeasureOnPreviewCopy(target, (copy, accumulator) => accumulator.AddCurrentPose(), out bounds);
        }

        /// <summary>
        /// プレビューシーンに原点・単位スケールで複製を置き、measure の中で AddCurrentPose された全ポーズの頂点を包含する AABB を返す。
        /// </summary>
        private static bool MeasureOnPreviewCopy(AnimatorLod target, System.Action<GameObject, PoseAccumulator> measure, out Bounds bounds)
        {
            bounds = default;
            Scene preview = EditorSceneManager.NewPreviewScene();
            GameObject copy = null;
            PoseAccumulator accumulator = null;
            try
            {
                copy = Object.Instantiate(target.gameObject);
                copy.hideFlags = HideFlags.HideAndDontSave;
                SceneManager.MoveGameObjectToScene(copy, preview);
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                copy.transform.localScale = Vector3.one;

                var renderers = copy.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (renderers.Length == 0)
                {
                    Debug.LogError("[AnimatorLod] SkinnedMeshRenderer が見つかりません。", target);
                    return false;
                }

                accumulator = new PoseAccumulator(copy.transform, renderers);
                measure(copy, accumulator);

                if (!accumulator.TryGetBounds(out bounds))
                {
                    Debug.LogError("[AnimatorLod] 頂点を取得できませんでした。", target);
                    return false;
                }
                return true;
            }
            finally
            {
                accumulator?.Dispose();
                if (copy != null)
                {
                    Object.DestroyImmediate(copy);
                }
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        /// <summary>現在のポーズで各 SMR をベイクし、ルートローカル空間の頂点の最小・最大を積算する。</summary>
        private sealed class PoseAccumulator : System.IDisposable
        {
            private readonly Transform _root;
            private readonly SkinnedMeshRenderer[] _renderers;
            private readonly Mesh _baked = new Mesh();
            private readonly List<Vector3> _verts = new List<Vector3>(4096);
            private bool _any;
            private float _minX = float.PositiveInfinity, _minY = float.PositiveInfinity, _minZ = float.PositiveInfinity;
            private float _maxX = float.NegativeInfinity, _maxY = float.NegativeInfinity, _maxZ = float.NegativeInfinity;

            public PoseAccumulator(Transform root, SkinnedMeshRenderer[] renderers)
            {
                _root = root;
                _renderers = renderers;
            }

            public void AddCurrentPose()
            {
                for (int r = 0; r < _renderers.Length; r++)
                {
                    var smr = _renderers[r];
                    if (smr.sharedMesh == null)
                    {
                        continue;
                    }
                    smr.BakeMesh(_baked);
                    _baked.GetVertices(_verts);
                    var m = _root.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                    // 頂点数 × サンプル数回まわるため、MultiplyPoint3x4 と Vector3.Min/Max を展開する
                    // (演算順は MultiplyPoint3x4 と同じなので結果は一致する)
                    int count = _verts.Count;
                    for (int v = 0; v < count; v++)
                    {
                        var q = _verts[v];
                        float x = m.m00 * q.x + m.m01 * q.y + m.m02 * q.z + m.m03;
                        float y = m.m10 * q.x + m.m11 * q.y + m.m12 * q.z + m.m13;
                        float z = m.m20 * q.x + m.m21 * q.y + m.m22 * q.z + m.m23;
                        if (x < _minX) _minX = x;
                        if (x > _maxX) _maxX = x;
                        if (y < _minY) _minY = y;
                        if (y > _maxY) _maxY = y;
                        if (z < _minZ) _minZ = z;
                        if (z > _maxZ) _maxZ = z;
                    }
                    _any |= count > 0;
                }
            }

            public bool TryGetBounds(out Bounds bounds)
            {
                if (!_any)
                {
                    bounds = default;
                    return false;
                }
                var min = new Vector3(_minX, _minY, _minZ);
                var max = new Vector3(_maxX, _maxY, _maxZ);
                bounds = new Bounds((min + max) * 0.5f, max - min);
                return true;
            }

            public void Dispose()
            {
                Object.DestroyImmediate(_baked);
            }
        }
    }
}
