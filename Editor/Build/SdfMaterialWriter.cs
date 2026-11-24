using UnityEngine;

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    internal static class SdfMaterialWriter
    {
        /// <summary>
        /// 対象マテリアルを複製し、このツールが所有するプロパティだけを書き込む。
        /// `_ShadowMaskType` = 2（SDF）、`_ShadowStrengthMask` = 生成テクスチャ。
        /// 元のマテリアルと他のプロパティには触れない。特に影の設定（強さ・ぼかし・境界など）は、
        /// ユーザーが SDF を見ながら決める値なので書き換えない。
        /// </summary>
        public static Material CreateSdfMaterial(Material source, Texture2D sdf)
        {
            var m = new Material(source) { name = source.name + "_SDF" };
            m.SetFloat("_ShadowMaskType", 2f);
            m.SetTexture("_ShadowStrengthMask", sdf);
            return m;
        }
    }
}
