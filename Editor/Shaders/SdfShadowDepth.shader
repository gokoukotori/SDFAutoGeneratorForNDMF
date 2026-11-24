// SdfLightBaker 用: 影を落とす三角形を、光の向きに沿った正射影で描き、テクセルごとに光にいちばん近い奥行きを残す（BlendOp Min）。
// 描く先は RFloat。1e9 でクリアしておく（何も描かれないテクセルは 1e9 のまま）。
// _SdfAutoGen_LightMatrix は frame 空間の位置を (x, y, 奥行き) に移す。x, y は −1〜1、奥行きは光から遠いほど大きい frame 空間の長さ。
// _SdfAutoGen_LightMatrix・_SdfAutoGen_FlipY は CommandBuffer の SetGlobal で渡す（マテリアルの値で上書きされないよう Properties には書かない）。
Shader "Hidden/SdfAutoGen/ShadowDepth"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        ZTest Always
        ZWrite Off
        BlendOp Min
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // 最初の描画からこのシェーダーで描かれるよう、非同期コンパイル（代わりのシェーダーで描く）を使わない
            #pragma editor_sync_compilation

            float4x4 _SdfAutoGen_LightMatrix;
            float _SdfAutoGen_FlipY;

            struct appdata
            {
                float3 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 ls = mul(_SdfAutoGen_LightMatrix, float4(v.vertex, 1.0)).xyz;
                // 受ける側は (x, y) * 0.5 + 0.5 を UV として読む。
                // UV 空間の描画（LightAccumulate）と同じく y に _SdfAutoGen_FlipY を掛け、どの API でも UV の v = 0 が y = −1 になるようにする
                o.pos = float4(ls.x, ls.y * _SdfAutoGen_FlipY, 0.5, 1.0);
                o.depth = ls.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, 0.0, 0.0, 0.0);
            }
            ENDCG
        }
    }
}
