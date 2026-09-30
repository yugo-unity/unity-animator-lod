using System.Text;
using UnityEngine;

namespace AnimatorLodTest
{
    /// <summary>
    /// HUD 文字列を GC Alloc なしで組み立てるための補助。
    /// float.ToString / enum.ToString は毎回文字列を生成するので、整数演算で StringBuilder に直接書く。
    /// </summary>
    public static class HudText
    {
        private static readonly int[] Pow10 = { 1, 10, 100, 1000, 10000 };

        /// <summary>value を小数点以下 decimals 桁の固定小数で追記する(ToString("F<n>") の非アロケート版)。</summary>
        public static void AppendFixed(StringBuilder sb, float value, int decimals)
        {
            decimals = Mathf.Clamp(decimals, 0, Pow10.Length - 1);
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                sb.Append('-');
                return;
            }

            if (value < 0f)
            {
                sb.Append('-');
                value = -value;
            }

            int scale = Pow10[decimals];
            long scaled = (long)(value * scale + 0.5f);
            long integer = scaled / scale;
            long fraction = scaled % scale;

            sb.Append(integer);
            if (decimals > 0)
            {
                sb.Append('.');
                for (int d = decimals - 1; d >= 1; d--)
                {
                    if (fraction < Pow10[d])
                    {
                        sb.Append('0');
                    }
                }
                sb.Append(fraction);
            }
        }

        /// <summary>AnimatorCullingMode の名前をリテラルで返す(enum.ToString のアロケート回避)。</summary>
        public static string CullingModeName(AnimatorCullingMode mode)
        {
            switch (mode)
            {
                case AnimatorCullingMode.AlwaysAnimate: return "AlwaysAnimate";
                case AnimatorCullingMode.CullUpdateTransforms: return "CullUpdateTransforms";
                case AnimatorCullingMode.CullCompletely: return "CullCompletely";
                default: return "Unknown";
            }
        }

        public static string OnOff(bool value) => value ? "ON" : "OFF";
    }
}
