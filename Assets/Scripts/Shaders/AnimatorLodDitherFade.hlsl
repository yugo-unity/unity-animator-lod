#ifndef ANIMATOR_LOD_DITHER_FADE_INCLUDED
#define ANIMATOR_LOD_DITHER_FADE_INCLUDED

// AnimatorLod の Near / Far ディザフェード用 Custom Function(Shader Graph の File モードで使う)。
//
// - フェード量は Renderer Shader User Value(RSUV)の下位 8 bit。0 = 完全表示、255 = 完全に消えた。
// - ディザは URP の LOD Cross Fade と同じテクスチャ(_DitheringTexture。URP Asset の Dithering Type に従う)と
//   同じ比較(LODFadeCrossFade で unity_LODFade.x が正のとき)を使う。
// - clip するのは ANIMATOR_LOD_DITHER_FADE が有効なバリアントだけ。AnimatorLod はフェード中だけ、
//   このキーワードを有効にしたマテリアルに差し替える。キーワードは Shader Graph の Blackboard で
//   Boolean / Multi Compile / Local として宣言する(Shader Feature だとビルドで有効側が落ちる)。
// - 影・Depth 系のパスでも clip させるため、Alpha ブロックにつなぐ(URP Lit では Alpha ブロックは
//   Alpha Clipping / Transparent / Allow Material Override のいずれかが有効なときだけ全パスで生成される)。

#if defined(ANIMATOR_LOD_DITHER_FADE) && !defined(SHADERGRAPH_PREVIEW)
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

void AnimatorLodDitherFade_float(float Alpha, float2 PixelPosition, out float Out)
{
#if defined(ANIMATOR_LOD_DITHER_FADE) && !defined(SHADERGRAPH_PREVIEW)
    float visible = 1.0 - (float)(unity_RendererUserValue & 0xFFu) * (1.0 / 255.0);
    half d = SAMPLE_TEXTURE2D(_DitheringTexture, sampler_PointRepeat, PixelPosition * _DitheringTextureInvSize).a;
    clip(visible - d);
#endif
    Out = Alpha;
}

void AnimatorLodDitherFade_half(half Alpha, half2 PixelPosition, out half Out)
{
    float result;
    AnimatorLodDitherFade_float(Alpha, PixelPosition, result);
    Out = (half)result;
}

#endif
