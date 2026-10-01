using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace AnimatorLodTest.Editor
{
    /// <summary>
    /// ビルドに含まれる全 URP Asset で LOD Cross Fade が無効なら、<see cref="AnimatorLod.DitherFadeKeyword"/> が有効なバリアントを取り除く。
    /// そのときランタイムは Dither Fade を無効として扱い、フェード用マテリアルに差し替えないので、取り除いたバリアントは要求されない。
    /// 判定は URP 自身のストリップ(全 Asset の論理和)に合わせる。1 つでも有効な Asset があれば残す。
    /// 判定は呼ばれるたびに行う(キーワードを持つシェーダーだけ)。AssetBundle のビルドでは IPreprocessBuildWithReport /
    /// IPostprocessBuildWithReport が呼ばれないため、ビルド単位で判定を覚えておくと前のビルドの結果が残る。
    /// </summary>
    internal sealed class AnimatorLodDitherFadeStripper : IPreprocessShaders
    {
        public int callbackOrder => 0;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            var keyword = shader.keywordSpace.FindKeyword(AnimatorLod.DitherFadeKeyword);
            if (!keyword.isValid || !ShouldStrip(EditorUserBuildSettings.activeBuildTarget))
            {
                return;
            }
            for (int i = data.Count - 1; i >= 0; i--)
            {
                if (data[i].shaderKeywordSet.IsEnabled(keyword))
                {
                    data.RemoveAt(i);
                }
            }
        }

        /// <summary>target のビルドに含まれる URP Asset がすべて LOD Cross Fade 無効なら true。URP Asset が見つからなければ false(取り除かない)。</summary>
        internal static bool ShouldStrip(BuildTarget target)
        {
            var assets = new List<UniversalRenderPipelineAsset>();
            if (!target.TryGetRenderPipelineAssets(assets))
            {
                return false;
            }
            foreach (var asset in assets)
            {
                if (asset.enableLODCrossFade)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
