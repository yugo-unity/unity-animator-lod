using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnimatorStressTest.Editor
{
    /// <summary>
    /// コントローラの全クリップを全フレームサンプリングし、
    /// SkinnedMeshRenderer をベイクした頂点を包含する AABB(ルート Transform ローカル空間)を求める。
    /// プレビューシーン上の一時複製で行うため、対象がプレハブアセットでもシーン上でも元を汚さない。
    /// </summary>
    public static class AnimatorLodBoundsCalculator
    {
        private const int MaxSamplesPerClip = 600;

        /// <summary>コンポーネントのアタッチ時(AnimatorLod.Reset)も Calculate Bounding と同じ計算にする。</summary>
        [InitializeOnLoadMethod]
        private static void RegisterResetHook()
        {
            AnimatorLod.EditorResetBoundsHook = lod => Apply(lod, false);
        }

        /// <summary>
        /// Calculate Bounding の本体。計算して AnimatorLod の Bounds に書き込む(Inspector のボタンとアタッチ時で共通)。
        /// recordUndo はボタン操作時のみ(アタッチ時は Add Component の Undo に含まれる)。
        /// </summary>
        public static bool Apply(AnimatorLod lod, bool recordUndo)
        {
            if (!Calculate(lod, out var bounds, out int samples, out bool fromClips))
            {
                return false;
            }
            if (recordUndo)
            {
                Undo.RecordObject(lod, "Calculate Bounding");
            }
            lod.EditorSetCalculatedBounds(bounds, fromClips);
            EditorUtility.SetDirty(lod);
            PrefabUtility.RecordPrefabInstancePropertyModifications(lod);
            Debug.Log(fromClips
                ? $"[AnimatorLod] {lod.name}: bounds center={bounds.center} size={bounds.size} ({samples} samples)"
                : $"[AnimatorLod] {lod.name}: no Animator Controller, bounds taken from the current renderers. center={bounds.center} size={bounds.size}", lod);
            return true;
        }

        /// <summary>
        /// fromClips = true: コントローラの全クリップから実測。
        /// Animator に Controller が無い場合はエラーにせず、現在の Renderer の Bounds を返す(fromClips = false)。
        /// </summary>
        public static bool Calculate(AnimatorLod target, out Bounds bounds, out int sampleCount, out bool fromClips)
        {
            bounds = default;
            sampleCount = 0;
            fromClips = false;

            var sourceAnimator = target.GetComponent<Animator>();
            var controller = sourceAnimator != null ? sourceAnimator.runtimeAnimatorController : null;
            if (controller == null)
            {
                return target.EditorComputeRendererBounds(out bounds);
            }
            fromClips = true;

            if (!sourceAnimator.hasTransformHierarchy)
            {
                // Optimize Game Objects 済みリグは AnimationMode でサンプリングできない
                // ("Optimized Game Object are not supported for sampling")。
                Debug.LogError("[AnimatorLod] Optimize Game Objects が有効なリグは実測できません。" +
                               "最適化前の同一スケルトンで Calculate Bounding を行い、その Bounds を手動でコピーしてください。", target);
                return false;
            }

            var clips = new HashSet<AnimationClip>(controller.animationClips);
            clips.Remove(null);
            if (clips.Count == 0)
            {
                Debug.LogError("[AnimatorLod] Controller にクリップがありません。", target);
                return false;
            }

            Scene preview = EditorSceneManager.NewPreviewScene();
            GameObject copy = null;
            var baked = new Mesh();
            bool started = false;
            try
            {
                copy = Object.Instantiate(target.gameObject);
                copy.hideFlags = HideFlags.HideAndDontSave;
                SceneManager.MoveGameObjectToScene(copy, preview);
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                copy.transform.localScale = Vector3.one;

                var root = copy.transform;
                var renderers = copy.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (renderers.Length == 0)
                {
                    Debug.LogError("[AnimatorLod] SkinnedMeshRenderer が見つかりません。", target);
                    return false;
                }

                AnimationMode.StartAnimationMode();
                started = true;

                bool any = false;
                float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
                float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
                var verts = new List<Vector3>(4096);

                foreach (var clip in clips)
                {
                    float frameRate = clip.frameRate > 0f ? clip.frameRate : 30f;
                    int steps = Mathf.Clamp(Mathf.CeilToInt(clip.length * frameRate), 1, MaxSamplesPerClip);
                    for (int s = 0; s <= steps; s++)
                    {
                        float t = clip.length * s / steps;
                        AnimationMode.SampleAnimationClip(copy, clip, t);
                        sampleCount++;

                        for (int r = 0; r < renderers.Length; r++)
                        {
                            var smr = renderers[r];
                            if (smr.sharedMesh == null)
                            {
                                continue;
                            }
                            smr.BakeMesh(baked);
                            baked.GetVertices(verts);
                            var m = root.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                            // 頂点数 × サンプル数回まわるため、MultiplyPoint3x4 と Vector3.Min/Max を展開する
                            // (演算順は MultiplyPoint3x4 と同じなので結果は一致する)
                            int count = verts.Count;
                            for (int v = 0; v < count; v++)
                            {
                                var q = verts[v];
                                float x = m.m00 * q.x + m.m01 * q.y + m.m02 * q.z + m.m03;
                                float y = m.m10 * q.x + m.m11 * q.y + m.m12 * q.z + m.m13;
                                float z = m.m20 * q.x + m.m21 * q.y + m.m22 * q.z + m.m23;
                                if (x < minX) minX = x;
                                if (x > maxX) maxX = x;
                                if (y < minY) minY = y;
                                if (y > maxY) maxY = y;
                                if (z < minZ) minZ = z;
                                if (z > maxZ) maxZ = z;
                            }
                            any |= count > 0;
                        }
                    }
                }

                if (!any)
                {
                    Debug.LogError("[AnimatorLod] 頂点を取得できませんでした。", target);
                    return false;
                }

                var min = new Vector3(minX, minY, minZ);
                var max = new Vector3(maxX, maxY, maxZ);
                bounds = new Bounds((min + max) * 0.5f, max - min);
                return true;
            }
            finally
            {
                if (started)
                {
                    AnimationMode.StopAnimationMode();
                }
                Object.DestroyImmediate(baked);
                if (copy != null)
                {
                    Object.DestroyImmediate(copy);
                }
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }
    }
}
