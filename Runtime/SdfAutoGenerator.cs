using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Gokoukotori.SdfAutoGenerator
{
    public enum SdfTargetMode
    {
        Specified,
        All,
    }

    public enum SdfResolution
    {
        [InspectorName("512")] R512 = 512,
        [InspectorName("1024")] R1024 = 1024,
        [InspectorName("2048")] R2048 = 2048,
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Gokoukotori/SDF Auto Generator")]
    public class SdfAutoGenerator : MonoBehaviour, INDMFEditorOnly
    {
        public SdfTargetMode targetMode = SdfTargetMode.Specified;
        public List<Material> materials = new List<Material>();
        // 影の設定（強さ・ぼかし・境界）は持たない。ユーザーがマテリアル側で SDF を見ながら決める値のため

        /// <summary>光の高さの上限（度）。90° 以上では後ろ向きの面の判定の前提が崩れる。</summary>
        public const float MaxLightElevation = 89f;

        /// <summary>法線のなめらかさの上限（回）。大きすぎると平滑化でエディターが止まる。</summary>
        public const int MaxNormalSmoothing = 50;

        /// <summary>光の高さ（度）。光が水平からどれだけ上から当たる前提で影を作るか。</summary>
        [Range(0f, MaxLightElevation)]
        public float lightElevation = 15f;

        /// <summary>法線をならす回数。0 は平滑化なし。</summary>
        [Range(0, MaxNormalSmoothing)]
        public int normalSmoothing = 5;

        /// <summary>落ち影の届く距離（ワールドの m）。0 で落ち影なし。</summary>
        [Min(0f)]
        public float shadowDistance = 0.03f;

        public SdfResolution resolution = SdfResolution.R1024;

        // 空の Start() を置くことで、インスペクタに有効チェックボックスを出す。
        private void Start()
        {
        }
    }
}
