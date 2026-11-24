using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    internal readonly struct SdfBakeParams
    {
        public readonly int Size;
        /// <summary>光の高さ α（度）。</summary>
        public readonly float ElevationDeg;
        /// <summary>落ち影の届く距離（SDF 座標系の長さ）。0 で落ち影なし。</summary>
        public readonly float ShadowDistance;

        public SdfBakeParams(int size, float elevationDeg, float shadowDistance)
        {
            Size = size;
            ElevationDeg = elevationDeg;
            ShadowDistance = shadowDistance;
        }
    }

    internal sealed class SdfLightBake
    {
        public int Size;
        /// <summary>−X 側の光で明るかった割合（0〜1）。添字は y * Size + x（y = 0 が UV の v = 0 側）。Coverage が 0 のテクセルは 0。</summary>
        public float[] R;
        /// <summary>+X 側の光で明るかった割合（0〜1）。添字は R と同じ。</summary>
        public float[] G;
        /// <summary>テクセルに重なった三角形の枚数。</summary>
        public float[] Coverage;
    }

    /// <summary>
    /// 光の角度ごとにメッシュを UV 空間へ描いて加算し、テクセルごとに「全角度のうち明るかった割合」を求める。
    /// θ_k = (k + 0.5)·180°/N の N 回描く。
    /// 落ち影ありなら、角度ごとに −X 側・+X 側の光のシャドウマップを描いてから、UV 空間の描画で判定する。
    /// </summary>
    internal static class SdfLightBaker
    {
        public const int AngleSteps = 180;
        /// <summary>シャドウマップの解像度（1 辺）。</summary>
        public const int ShadowMapSize = 1024;
        /// <summary>繰り返し描く UV タイルの上限。</summary>
        public const int MaxUvTiles = 64;

        /// <summary>判定の前に位置を法線方向へずらす量（シャドウマップのテクセル数）。</summary>
        const float NormalOffsetTexels = 2f;
        /// <summary>シャドウマップのクリア値（何も描かれないテクセル = 遮るものなし）。</summary>
        const float FarDepth = 1e9f;

        const string AccumulateShaderName = "Hidden/SdfAutoGen/LightAccumulate";
        const string ShadowDepthShaderName = "Hidden/SdfAutoGen/ShadowDepth";

        // ほかのシェーダーのグローバル値とぶつからないよう、すべて _SdfAutoGen_ で始める
        static readonly int LightRId = Shader.PropertyToID("_SdfAutoGen_LightR");
        static readonly int LightGId = Shader.PropertyToID("_SdfAutoGen_LightG");
        static readonly int UvOffsetId = Shader.PropertyToID("_SdfAutoGen_UvOffset");
        static readonly int FlipYId = Shader.PropertyToID("_SdfAutoGen_FlipY");
        static readonly int LightMatrixId = Shader.PropertyToID("_SdfAutoGen_LightMatrix");
        static readonly int LightMatrixRId = Shader.PropertyToID("_SdfAutoGen_LightMatrixR");
        static readonly int LightMatrixGId = Shader.PropertyToID("_SdfAutoGen_LightMatrixG");
        static readonly int ShadowMapRId = Shader.PropertyToID("_SdfAutoGen_ShadowMapR");
        static readonly int ShadowMapGId = Shader.PropertyToID("_SdfAutoGen_ShadowMapG");
        static readonly int ShadowDistanceId = Shader.PropertyToID("_SdfAutoGen_ShadowDistance");
        static readonly int ShadowTexelId = Shader.PropertyToID("_SdfAutoGen_ShadowTexel");
        static readonly int ShadowNormalOffsetId = Shader.PropertyToID("_SdfAutoGen_ShadowNormalOffset");

        /// <param name="casterTriangles">影を落とす三角形（sample.Positions の添字）。空なら落ち影なし。</param>
        public static SdfLightBake Bake(SdfMeshSample sample, Vector3[] normals, int[] casterTriangles, in SdfBakeParams p)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            var positions = sample.Positions ?? new Vector3[0];
            if (normals == null || normals.Length != positions.Length)
                throw new ArgumentException($"normals の数（{normals?.Length ?? 0}）が頂点の数（{positions.Length}）と違う", nameof(normals));
            var uvs = sample.Uvs;
            if (uvs == null || uvs.Length != positions.Length)
                throw new ArgumentException($"UV の数（{uvs?.Length ?? 0}）が頂点の数（{positions.Length}）と違う", nameof(sample));

            int n = p.Size;
            var result = new SdfLightBake
            {
                Size = n,
                R = new float[n * n],
                G = new float[n * n],
                Coverage = new float[n * n],
            };

            var tris = sample.Triangles ?? new int[0];
            int triCount = tris.Length / 3 * 3;
            if (triCount == 0) return result;

            // 描く三角形が使う UV の範囲から、繰り返し描く整数タイルを決める
            UvTiles(uvs, tris, triCount, out int tileU0, out int tileU1, out int tileV0, out int tileV1);

            // 落ち影: 影を落とす三角形の境界ボックスを含む球（中心 C、半径 Rr）を、光の向きに沿って正射影する
            var casters = casterTriangles ?? new int[0];
            int casterCount = casters.Length / 3 * 3;
            bool shadows = false;
            Vector3 center = Vector3.zero;
            float radius = 0f;
            if (p.ShadowDistance > 0f && casterCount > 0)
            {
                var cmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var cmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int t = 0; t < casterCount; t++)
                {
                    int idx = casters[t];
                    if (idx < 0 || idx >= positions.Length)
                        throw new ArgumentException($"casterTriangles の添字 {idx} が頂点の範囲外", nameof(casterTriangles));
                    cmin = Vector3.Min(cmin, positions[idx]);
                    cmax = Vector3.Max(cmax, positions[idx]);
                }

                center = (cmin + cmax) * 0.5f;
                radius = ((cmax - cmin) * 0.5f).magnitude * 1.05f;
                // 大きさの無い（または有限でない）影を落とす側は、落ち影なしとして扱う（0 で割らない）
                shadows = radius > 1e-12f && !float.IsInfinity(radius);
            }

            float shadowTexel = shadows ? 2f * radius / ShadowMapSize : 0f;

            var accumulateShader = FindSupportedShader(AccumulateShaderName);
            Shader depthShader = shadows ? FindSupportedShader(ShadowDepthShaderName) : null;

            Mesh mesh = null;
            Material material = null;
            Material depthMaterial = null;
            RenderTexture rt = null;
            RenderTexture shadowMapR = null;
            RenderTexture shadowMapG = null;
            Texture2D readback = null;
            CommandBuffer cmd = null;
            var prevActive = RenderTexture.active;
            try
            {
                // サブメッシュ 0 = UV 空間に描く三角形、1 = 影を落とす三角形
                mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                mesh.indexFormat = positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.vertices = positions;
                mesh.normals = normals;
                mesh.uv = uvs;
                mesh.subMeshCount = shadows ? 2 : 1;
                mesh.SetTriangles(tris, 0, triCount, 0, false);
                if (shadows) mesh.SetTriangles(casters, 0, casterCount, 1, false);

                material = new Material(accumulateShader) { hideFlags = HideFlags.HideAndDontSave };

                rt = CreateTarget(n, RenderTextureFormat.ARGBFloat);

                cmd = new CommandBuffer { name = "SdfLightBake" };
                cmd.SetRenderTarget(rt);
                cmd.ClearRenderTarget(false, true, Color.clear);
                // UV の v = 0 をテクセルの y = 0（読み戻した配列の先頭行）に描く。
                // 上端が UV の 0 の API（D3D・Metal・Vulkan）では、クリップ座標の y = +1 が y = 0 の行になるので反転する
                cmd.SetGlobalFloat(FlipYId, SystemInfo.graphicsUVStartsAtTop ? -1f : 1f);
                // 0 なら LightAccumulate は落ち影を判定しない
                cmd.SetGlobalFloat(ShadowDistanceId, shadows ? p.ShadowDistance : 0f);

                if (shadows)
                {
                    depthMaterial = new Material(depthShader) { hideFlags = HideFlags.HideAndDontSave };
                    shadowMapR = CreateTarget(ShadowMapSize, RenderTextureFormat.RFloat);
                    shadowMapG = CreateTarget(ShadowMapSize, RenderTextureFormat.RFloat);
                    cmd.SetGlobalFloat(ShadowTexelId, shadowTexel);
                    cmd.SetGlobalFloat(ShadowNormalOffsetId, NormalOffsetTexels * shadowTexel);
                    cmd.SetGlobalTexture(ShadowMapRId, shadowMapR);
                    cmd.SetGlobalTexture(ShadowMapGId, shadowMapG);
                }

                float alpha = p.ElevationDeg * Mathf.Deg2Rad;
                float cosA = Mathf.Cos(alpha);
                float sinA = Mathf.Sin(alpha);
                var far = new Color(FarDepth, FarDepth, FarDepth, FarDepth);
                for (int k = 0; k < AngleSteps; k++)
                {
                    float theta = (k + 0.5f) * Mathf.PI / AngleSteps;
                    float sx = Mathf.Sin(theta) * cosA;
                    float z = Mathf.Cos(theta) * cosA;
                    var lightR = new Vector3(-sx, sinA, z);
                    var lightG = new Vector3(sx, sinA, z);

                    if (shadows)
                    {
                        // この角度のシャドウマップを描き直す（−X 側、+X 側の順）
                        var matrixR = LightMatrix(lightR, center, radius);
                        var matrixG = LightMatrix(lightG, center, radius);
                        DrawShadowMap(cmd, shadowMapR, far, matrixR, mesh, depthMaterial);
                        DrawShadowMap(cmd, shadowMapG, far, matrixG, mesh, depthMaterial);
                        cmd.SetGlobalMatrix(LightMatrixRId, matrixR);
                        cmd.SetGlobalMatrix(LightMatrixGId, matrixG);
                        // 足し込み先へ戻る（中身は残す）
                        cmd.SetRenderTarget(rt, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                    }

                    cmd.SetGlobalVector(LightRId, lightR);
                    cmd.SetGlobalVector(LightGId, lightG);
                    for (int tv = tileV0; tv <= tileV1; tv++)
                    for (int tu = tileU0; tu <= tileU1; tu++)
                    {
                        // タイル (tu, tv) にある部分を 0〜1 に移す
                        cmd.SetGlobalVector(UvOffsetId, new Vector4(-tu, -tv, 0f, 0f));
                        cmd.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0);
                    }
                }

                Graphics.ExecuteCommandBuffer(cmd);

                readback = new Texture2D(n, n, TextureFormat.RGBAFloat, false, true) { hideFlags = HideFlags.HideAndDontSave };
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, n, n), 0, 0, false);
                var px = readback.GetPixels();
                for (int i = 0; i < n * n; i++)
                {
                    var c = px[i];
                    if (c.b <= 0f) continue;
                    result.R[i] = c.r / c.b;
                    result.G[i] = c.g / c.b;
                    result.Coverage[i] = c.b / AngleSteps;
                }
            }
            finally
            {
                RenderTexture.active = prevActive;
                cmd?.Release();
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
                ReleaseTarget(rt);
                ReleaseTarget(shadowMapR);
                ReleaseTarget(shadowMapG);
                if (depthMaterial != null) UnityEngine.Object.DestroyImmediate(depthMaterial);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            }

            return result;
        }

        /// <summary>
        /// シェーダーを名前で探す。見つからない、またはこの GPU・グラフィックス API で使えない（何も描かれない）ときは
        /// InvalidOperationException（値の無いテクスチャを黙って作らないため）。
        /// </summary>
        static Shader FindSupportedShader(string name)
        {
            var shader = Shader.Find(name);
            if (shader == null) throw new InvalidOperationException($"シェーダー {name} が見つからない");
            if (!shader.isSupported)
                throw new InvalidOperationException($"シェーダー {name} がこの GPU・グラフィックス API で使えない");
            return shader;
        }

        static bool IsFinite(float f)
        {
            return !float.IsNaN(f) && !float.IsInfinity(f);
        }

        /// <summary>
        /// 三角形（tris の先頭 triCount 個の添字）が使う UV の範囲から、繰り返し描く整数タイルの範囲（両端を含む）を決める。
        /// UV が有限でない、またはタイルが <see cref="MaxUvTiles"/> 枚を超えるときは ArgumentException。
        /// SdfTextureGenerator も CPU のラスタライズの前に同じ判定をする（極端な UV で止まらないように）。
        /// </summary>
        internal static void UvTiles(Vector2[] uvs, int[] tris, int triCount,
            out int tileU0, out int tileU1, out int tileV0, out int tileV1)
        {
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            for (int t = 0; t < triCount; t++)
            {
                var uv = uvs[tris[t]];
                if (!IsFinite(uv.x) || !IsFinite(uv.y))
                    throw new ArgumentException($"UV が有限でない（頂点 {tris[t]}: {uv.x}, {uv.y}）", "sample");
                minU = Mathf.Min(minU, uv.x);
                maxU = Mathf.Max(maxU, uv.x);
                minV = Mathf.Min(minV, uv.y);
                maxV = Mathf.Max(maxV, uv.y);
            }

            tileU0 = TileRange(minU, maxU, out long tilesU);
            tileV0 = TileRange(minV, maxV, out long tilesV);
            // 片方ずつ先に比べて、掛け算が桁あふれしないようにする
            if (tilesU > MaxUvTiles || tilesV > MaxUvTiles || tilesU * tilesV > MaxUvTiles)
                throw new ArgumentException(
                    $"UV の範囲が広すぎる（u {minU}〜{maxU}, v {minV}〜{maxV}: タイル {tilesU}×{tilesV} 枚 > {MaxUvTiles}）", "sample");
            tileU1 = tileU0 + (int)tilesU - 1;
            tileV1 = tileV0 + (int)tilesV - 1;
        }

        /// <summary>
        /// [min, max] を覆う整数タイルの最初の番号と枚数。上端は CeilToInt(max) − 1（[0, 1] は 1 枚）。
        /// 枚数は long で返す。タイルの番号が int に収まらないときは枚数を long.MaxValue にする（呼び出し側で上限を超えて例外になる）。
        /// </summary>
        static int TileRange(float min, float max, out long count)
        {
            double first = Math.Floor(min);
            double last = Math.Max(first, Math.Ceiling(max) - 1.0);
            if (first < int.MinValue || last > int.MaxValue)
            {
                count = long.MaxValue;
                return 0;
            }

            count = (long)(last - first) + 1;
            return (int)first;
        }

        static RenderTexture CreateTarget(int size, RenderTextureFormat format)
        {
            var target = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
            };
            if (!target.Create())
            {
                UnityEngine.Object.DestroyImmediate(target);
                throw new InvalidOperationException($"描画テクスチャ（{size}×{size}, {format}）を作れなかった");
            }

            return target;
        }

        static void ReleaseTarget(RenderTexture target)
        {
            if (target == null) return;
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
        }

        static void DrawShadowMap(CommandBuffer cmd, RenderTexture target, Color far, Matrix4x4 lightMatrix, Mesh mesh, Material depthMaterial)
        {
            cmd.SetRenderTarget(target, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.ClearRenderTarget(false, true, far);
            cmd.SetGlobalMatrix(LightMatrixId, lightMatrix);
            cmd.DrawMesh(mesh, Matrix4x4.identity, depthMaterial, 1, 0);
        }

        /// <summary>
        /// frame 空間 → 光の空間 (x, y, 奥行き)。f = −L、r = normalize(cross(up, f))、u = cross(f, r)。
        /// x = dot(P−C, r)/Rr、y = dot(P−C, u)/Rr、奥行き = dot(P−C, f)（光から遠いほど大きい）。
        /// </summary>
        internal static Matrix4x4 LightMatrix(Vector3 light, Vector3 center, float radius)
        {
            var f = -light.normalized;
            var up = Mathf.Abs(f.y) > 0.99f ? Vector3.right : Vector3.up;
            var r = Vector3.Cross(up, f).normalized;
            var u = Vector3.Cross(f, r);
            float inv = 1f / radius;
            var m = Matrix4x4.identity;
            m.SetRow(0, new Vector4(r.x * inv, r.y * inv, r.z * inv, -Vector3.Dot(center, r) * inv));
            m.SetRow(1, new Vector4(u.x * inv, u.y * inv, u.z * inv, -Vector3.Dot(center, u) * inv));
            m.SetRow(2, new Vector4(f.x, f.y, f.z, -Vector3.Dot(center, f)));
            m.SetRow(3, new Vector4(0f, 0f, 0f, 1f));
            return m;
        }
    }
}
