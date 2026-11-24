using UnityEngine;

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    internal static class SdfEncoding
    {
        /// <summary>lilToon の影1 の判定（border はマテリアルの値）。影2・影3 もそれぞれの境界で同じ式になる。</summary>
        public static bool PredictLit(float value, float lightAngleRad, float border)
        {
            return Mathf.Cos(lightAngleRad) * 0.5f + value * 0.5f + 0.25f > border;
        }
    }
}
