// SdfLightBaker 用: メッシュを UV 空間に展開して描き、1 つの光の角度について
// R = −X 側の光で明るいか、G = +X 側の光で明るいか、B = カバー数（1）を加算する。
// 明るい = 法線が光を向いている かつ（落ち影ありなら）落ち影の外。
// 値はすべて CommandBuffer の SetGlobal で渡す（マテリアルの値で上書きされないよう Properties には書かない）:
//   _SdfAutoGen_LightR・_SdfAutoGen_LightG: 光の向き（frame 空間、光の方へ向く）
//   _SdfAutoGen_UvOffset: 描く UV タイルを 0〜1 に移すずらし
//   _SdfAutoGen_FlipY: 上端が UV の 0 の API では −1
//   _SdfAutoGen_ShadowDistance: 落ち影の届く距離（frame 空間の長さ）。0 なら落ち影を判定しない
//   _SdfAutoGen_ShadowTexel: シャドウマップの 1 テクセルの幅（frame 空間の長さ）。これ以下の奥行きの差は影にしない
//   _SdfAutoGen_ShadowNormalOffset: 判定の前に位置を法線方向へずらす量（frame 空間の長さ）
//   _SdfAutoGen_LightMatrixR・G: frame 空間 → 光の空間 (x, y, 奥行き)。SdfShadowDepth と同じ行列
//   _SdfAutoGen_ShadowMapR・G: SdfShadowDepth で描いた、光にいちばん近い奥行き
Shader "Hidden/SdfAutoGen/LightAccumulate"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        ZTest Always
        ZWrite Off
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            // 最初の描画からこのシェーダーで描かれるよう、非同期コンパイル（代わりのシェーダーで描く）を使わない
            #pragma editor_sync_compilation

            float4 _SdfAutoGen_LightR;
            float4 _SdfAutoGen_LightG;
            float4 _SdfAutoGen_UvOffset;
            float _SdfAutoGen_FlipY;

            float _SdfAutoGen_ShadowDistance;
            float _SdfAutoGen_ShadowTexel;
            float _SdfAutoGen_ShadowNormalOffset;
            float4x4 _SdfAutoGen_LightMatrixR;
            float4x4 _SdfAutoGen_LightMatrixG;
            sampler2D_float _SdfAutoGen_ShadowMapR;
            sampler2D_float _SdfAutoGen_ShadowMapG;

            struct appdata
            {
                float3 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 framePos : TEXCOORD0;
                float3 normal : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                // 整数タイルぶんずらした UV をそのままクリップ座標にする（ビュー・射影行列は使わない）
                float2 uv = v.uv + _SdfAutoGen_UvOffset.xy;
                o.pos = float4(uv.x * 2.0 - 1.0, (uv.y * 2.0 - 1.0) * _SdfAutoGen_FlipY, 0.5, 1.0);
                // SDF 座標系（frame 空間）の位置と法線。位置は落ち影で使う
                o.framePos = v.vertex;
                o.normal = v.normal;
                return o;
            }

            // 落ち影の中なら 1。シャドウマップの範囲外は影なし
            float InShadow(float3 pos, float4x4 lightMatrix, sampler2D_float shadowMap)
            {
                float3 ls = mul(lightMatrix, float4(pos, 1.0)).xyz;
                // SdfShadowDepth は y に _SdfAutoGen_FlipY を掛けて描いたので、ここでは (x, y) * 0.5 + 0.5 がそのまま UV になる
                float2 uv = ls.xy * 0.5 + 0.5;
                if (any(uv < 0.0) || any(uv > 1.0)) return 0.0;
                float nearest = tex2Dlod(shadowMap, float4(uv, 0.0, 0.0)).r;
                float d = ls.z - nearest;
                return (d > _SdfAutoGen_ShadowTexel && d < _SdfAutoGen_ShadowDistance) ? 1.0 : 0.0;
            }

            float4 frag(v2f i) : SV_Target
            {
                float len2 = dot(i.normal, i.normal);
                // 長さ 0 の法線は明るさ 0（正規化で NaN にしない）
                float valid = len2 > 1e-12 ? 1.0 : 0.0;
                float3 n = i.normal * rsqrt(max(len2, 1e-12));
                float litR = valid * (dot(n, _SdfAutoGen_LightR.xyz) > 0.0 ? 1.0 : 0.0);
                float litG = valid * (dot(n, _SdfAutoGen_LightG.xyz) > 0.0 ? 1.0 : 0.0);

                if (_SdfAutoGen_ShadowDistance > 0.0)
                {
                    // 自分自身への誤った影（シャドウアクネ）を防ぐため、法線方向へずらしてから判定する
                    float3 p = i.framePos + n * _SdfAutoGen_ShadowNormalOffset;
                    litR *= 1.0 - InShadow(p, _SdfAutoGen_LightMatrixR, _SdfAutoGen_ShadowMapR);
                    litG *= 1.0 - InShadow(p, _SdfAutoGen_LightMatrixG, _SdfAutoGen_ShadowMapG);
                }

                return float4(litR, litG, 1.0, 0.0);
            }
            ENDCG
        }
    }
}
