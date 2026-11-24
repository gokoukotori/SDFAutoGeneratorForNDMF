using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    internal readonly struct SdfGenerationParams
    {
        public readonly int Size;
        /// <summary>光の高さ α（度）。</summary>
        public readonly float LightElevationDeg;
        /// <summary>法線をならす回数（0 で平滑化なし）。</summary>
        public readonly int SmoothIterations;
        /// <summary>落ち影の届く距離（ワールドの m）。0 で落ち影なし。</summary>
        public readonly float ShadowDistanceMeters;

        // マテリアルの影の設定は受け取らない（テクスチャはマテリアルの値に依存しない）
        // 値はコンポーネントの範囲（Range・Min）に丸める（ビルド・プレビューのどちらから来ても同じ）。
        // 光の高さが 90° 以上だと後ろ向きの面の B（litMax）の前提が崩れ、なめらかさが大きすぎると平滑化でエディターが止まるため。
        // NaN は光の高さ 0・落ち影なしとして扱う
        public SdfGenerationParams(int size, float lightElevationDeg, int smoothIterations, float shadowDistanceMeters)
        {
            Size = size;
            LightElevationDeg = float.IsNaN(lightElevationDeg)
                ? 0f
                : Mathf.Clamp(lightElevationDeg, 0f, SdfAutoGenerator.MaxLightElevation);
            SmoothIterations = Mathf.Clamp(smoothIterations, 0, SdfAutoGenerator.MaxNormalSmoothing);
            ShadowDistanceMeters = shadowDistanceMeters > 0f ? shadowDistanceMeters : 0f;
        }
    }

    internal sealed class SdfGenerationResult
    {
        public int Size;
        public Color[] Pixels;
        /// <summary>0 空, 1 SDF, 2 左右共有, 3 パディング</summary>
        public byte[] State;
        public int Covered;
        public int Mirrored;
        public float MirroredRatio => Covered == 0 ? 0 : (float)Mirrored / Covered;
        /// <summary>左右共有として影を落とす側から外した三角形の数（調査用）。</summary>
        public int ExcludedCasters;
        /// <summary>そのうち、テクセルの中心を 1 つも覆わず、重心・頂点のテクセルで判定した三角形の数（調査用）。</summary>
        public int ExcludedSubTexelCasters;
    }

    /// <summary>
    /// Renderer × マテリアルの三角形から SDF テクスチャの中身を作る。
    /// R・G（全角度のうち明るかった割合）は GPU の焼き込み（<see cref="SdfLightBaker"/>）から取る。
    /// カバー・左右共有（B = 1）・後ろ向きの面の B・パディングは CPU のラスタライズで決める。
    /// </summary>
    internal static class SdfTextureGenerator
    {
        /// <summary>
        /// 後ろ向きの面を通常の影へ混ぜるときの smoothstep の幅（N·L の値）。
        /// 「真正面からの光で暗い」と「ほかのどこかの角度で明るい」の両方を、0〜この値の間でなめらかに 0 → 1 にする
        /// （SDF の部分との境目に段差を出さないため）。
        /// </summary>
        public const float BackFacingBlend = 0.3f;

        public static SdfGenerationResult Generate(SdfMeshSample sample, in SdfGenerationParams p)
        {
            return Generate(sample, SdfProxyNormals.Build(sample, p.SmoothIterations), p);
        }

        /// <param name="normals">生成に使う法線（sample.Positions と同じ数）。GPU の焼き込みにも同じものを渡す。</param>
        public static SdfGenerationResult Generate(SdfMeshSample sample, Vector3[] normals, in SdfGenerationParams p)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            int n = p.Size;
            var tris = sample.Triangles ?? new int[0];
            int triCount = tris.Length / 3 * 3;
            var fPos = sample.Positions ?? new Vector3[0];
            var uvs = sample.Uvs;
            if (uvs == null || uvs.Length != fPos.Length)
                throw new ArgumentException($"UV の数（{uvs?.Length ?? 0}）が頂点の数（{fPos.Length}）と違う", nameof(sample));
            if (normals == null || normals.Length != fPos.Length)
                throw new ArgumentException($"normals の数（{normals?.Length ?? 0}）が頂点の数（{fPos.Length}）と違う", nameof(normals));

            // 焼き込みと同じ UV の判定を先に行う（有限でない・広すぎる UV で、CPU のラスタライズが止まらずにすぐ例外にする）
            if (triCount > 0) SdfLightBaker.UvTiles(uvs, tris, triCount, out _, out _, out _, out _);

            float minX = float.MaxValue, maxX = float.MinValue;
            for (int t = 0; t < triCount; t++)
            {
                minX = Mathf.Min(minX, fPos[tris[t]].x);
                maxX = Mathf.Max(maxX, fPos[tris[t]].x);
            }

            float cX = triCount > 0 ? (minX + maxX) * 0.5f : 0f;
            float eps = triCount > 0 ? (maxX - minX) * 0.01f : 0f;

            float alpha = p.LightElevationDeg * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(alpha);
            float sinA = Mathf.Sin(alpha);

            var sumB = new float[n * n];
            var count = new int[n * n];
            var side = new byte[n * n];
            var texels = new List<Texel>();

            // 1 回目: カバー数、左右判定のビット、後ろ向きの面の B を数える
            for (int t = 0; t < triCount; t += 3)
            {
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                float cx = (fPos[i0].x + fPos[i1].x + fPos[i2].x) / 3f - cX;
                byte sideBit = cx < -eps ? (byte)1 : cx > eps ? (byte)2 : (byte)4;

                Rasterize(uvs[i0] * n, uvs[i1] * n, uvs[i2] * n, n, texels);
                foreach (var tx in texels)
                {
                    var nrm = normals[i0] * tx.W0 + normals[i1] * tx.W1 + normals[i2] * tx.W2;
                    sumB[tx.Index] += BackFacingB(nrm, cosA, sinA);
                    count[tx.Index]++;
                    side[tx.Index] |= sideBit;
                }
            }

            int covered = 0;
            for (int i = 0; i < n * n; i++)
            {
                if (count[i] > 0) covered++;
            }

            // 2 回目: 左右共有のテクセルを 1 つでも覆う三角形は影を落とす側から外す（まつ毛・眉などの細く浮いた形）。
            // テクセルの中心を 1 つも覆わない小さな三角形（細いまつ毛など）は、重心と各頂点を含むテクセル（繰り返しで折り返す）の
            // どれかが左右共有なら外す（覆うテクセルが無いだけで影を落とす側に残ると、解像度によって影を落とす側が変わる）
            var casters = new List<int>(triCount);
            int excludedCasters = 0, excludedSubTexel = 0;
            for (int t = 0; t < triCount; t += 3)
            {
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                Rasterize(uvs[i0] * n, uvs[i1] * n, uvs[i2] * n, n, texels);
                bool mirrored = false;
                if (texels.Count > 0)
                {
                    foreach (var tx in texels)
                    {
                        if ((side[tx.Index] & 3) != 3) continue;
                        mirrored = true;
                        break;
                    }
                }
                else
                {
                    mirrored = IsMirroredAt(side, n, (uvs[i0] + uvs[i1] + uvs[i2]) / 3f) ||
                               IsMirroredAt(side, n, uvs[i0]) || IsMirroredAt(side, n, uvs[i1]) ||
                               IsMirroredAt(side, n, uvs[i2]);
                    if (mirrored) excludedSubTexel++;
                }

                if (mirrored)
                {
                    excludedCasters++;
                    continue;
                }

                casters.Add(i0);
                casters.Add(i1);
                casters.Add(i2);
            }

            // 1 テクセルも覆わなければ焼き込まない（結果は描画範囲なし）
            SdfLightBake bake = null;
            if (covered > 0)
            {
                // 落ち影の届く距離（ワールドの m）を frame の長さに直す。スケールが不正（0 以下・有限でない）なら 1 とみなす
                float scale = sample.WorldScale > 0f && !float.IsInfinity(sample.WorldScale) ? sample.WorldScale : 1f;
                float distance = p.ShadowDistanceMeters > 0f ? p.ShadowDistanceMeters / scale : 0f;
                bake = SdfLightBaker.Bake(sample, normals, casters.ToArray(),
                    new SdfBakeParams(n, p.LightElevationDeg, distance));
                EnsureGpuCoverage(bake, covered);
            }

            var result = new SdfGenerationResult
            {
                Size = n, ExcludedCasters = excludedCasters, ExcludedSubTexelCasters = excludedSubTexel,
            };
            var pixels = new Color[n * n];
            var state = new byte[n * n];
            for (int i = 0; i < n * n; i++)
            {
                if (count[i] == 0)
                {
                    pixels[i] = new Color(0.5f, 0.5f, 0f, 1f);
                    continue;
                }

                result.Covered++;
                bool mirrored = (side[i] & 3) == 3;
                if (mirrored) result.Mirrored++;
                SampleBake(bake, i, out float r, out float g);
                pixels[i] = new Color(r, g, mirrored ? 1f : sumB[i] / count[i], 1f);
                state[i] = mirrored ? (byte)2 : (byte)1;
            }

            Dilate(pixels, state, n, 16 * n / 2048);
            result.Pixels = pixels;
            result.State = state;
            return result;
        }

        /// <summary>UV（繰り返しで 0〜1 に折り返す）を含むテクセルが左右共有（両側の三角形が覆う）なら true。</summary>
        static bool IsMirroredAt(byte[] side, int n, Vector2 uv)
        {
            int x = Mathf.FloorToInt(uv.x * n) % n;
            int y = Mathf.FloorToInt(uv.y * n) % n;
            if (x < 0) x += n;
            if (y < 0) y += n;
            return (side[y * n + x] & 3) == 3;
        }

        /// <summary>
        /// CPU のラスタライズが覆ったテクセルがあるのに GPU の焼き込みが 1 テクセルも描かなかったら InvalidOperationException。
        /// シェーダーが動かない環境などで、値の無いテクスチャ（すべて埋め値の 0.5）を黙って作らないようにする
        /// （ビルドは GenerationFailed の警告にしてそのマテリアルだけ飛ばし、プレビューはコンソールに出す）。
        /// </summary>
        internal static void EnsureGpuCoverage(SdfLightBake bake, int cpuCovered)
        {
            if (cpuCovered <= 0) return;
            foreach (float c in bake.Coverage)
            {
                if (c > 0f) return;
            }

            throw new InvalidOperationException(
                $"GPU の焼き込みが 1 テクセルも描かなかった（CPU では {cpuCovered} テクセルを覆う）。GPU・シェーダーが使えない可能性がある");
        }

        /// <summary>
        /// GPU の値。CPU では覆っていて GPU では覆っていない（辺の扱いの差）テクセルは、8 近傍の GPU で覆ったテクセルの平均。
        /// それも無ければ 0.5。近傍は焼き込みの結果そのもの（埋めた値は使わない）。
        /// </summary>
        static void SampleBake(SdfLightBake bake, int idx, out float r, out float g)
        {
            if (bake.Coverage[idx] > 0f)
            {
                r = bake.R[idx];
                g = bake.G[idx];
                return;
            }

            int n = bake.Size;
            int x = idx % n;
            int y = idx / n;
            float sumR = 0f, sumG = 0f;
            int k = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int xx = x + dx;
                int yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= n || yy >= n) continue;
                int j = yy * n + xx;
                if (bake.Coverage[j] <= 0f) continue;
                sumR += bake.R[j];
                sumG += bake.G[j];
                k++;
            }

            r = k > 0 ? sumR / k : 0.5f;
            g = k > 0 ? sumG / k : 0.5f;
        }

        /// <summary>
        /// 後ろ向きの面の B（通常の影との混ぜ具合）。「真正面からの光で暗い、かつ、ほかのどこかの角度で明るい」面を通常の影にする。
        /// 正規化した法線 n、光の高さ α について、h = √(nx² + nz²)、
        /// f0 = nz·cos α + ny·sin α（真正面からの光の N·L。左右共通）、
        /// litMax = h·cos α + ny·sin α（左右・全角度での N·L の最大）として
        /// smoothstep(0, 幅, litMax) · smoothstep(0, 幅, −f0)。
        /// どの角度でも暗い面（あご下など下向きの面）は SDF のまま（値 0 で常に影）。向きの無い法線は 0。
        /// </summary>
        internal static float BackFacingB(Vector3 normal, float cosA, float sinA)
        {
            float len = normal.magnitude;
            if (len < 1e-6f) return 0f;
            float nx = normal.x / len;
            float ny = normal.y / len;
            float nz = normal.z / len;
            float h = Mathf.Sqrt(nx * nx + nz * nz);
            float f0 = nz * cosA + ny * sinA;
            float litMax = h * cosA + ny * sinA;
            return SmoothStep(0f, BackFacingBlend, litMax) * SmoothStep(0f, BackFacingBlend, -f0);
        }

        /// <summary>HLSL の smoothstep(e0, e1, x)（Mathf.SmoothStep は補間なので別物）。</summary>
        static float SmoothStep(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }

        readonly struct Texel
        {
            /// <summary>折り返し（UV の繰り返し）後のテクセルの添字 y * n + x。</summary>
            public readonly int Index;
            public readonly float W0, W1, W2;

            public Texel(int index, float w0, float w1, float w2)
            {
                Index = index;
                W0 = w0;
                W1 = w1;
                W2 = w2;
            }
        }

        /// <summary>
        /// 三角形 (a, b, d)（テクセル単位の UV）が中心を含むテクセルを、重心座標とともに texels に入れる（中身は入れ替える）。
        /// 辺上のテクセルは top-left 規則で片方の三角形にだけ属させる（巻き方向は面積の符号で正規化）。
        /// UV のはみ出しは繰り返し（タイル）として 0〜n−1 に折り返す。
        /// </summary>
        static void Rasterize(Vector2 a, Vector2 b, Vector2 d, int n, List<Texel> texels)
        {
            texels.Clear();
            float area = (b.x - a.x) * (d.y - a.y) - (d.x - a.x) * (b.y - a.y);
            if (Mathf.Abs(area) < 1e-12f) return;
            int bx0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, d.x)));
            int bx1 = Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, d.x)));
            int by0 = Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, d.y)));
            int by1 = Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, d.y)));
            float sgn = area > 0f ? 1f : -1f;
            bool own0 = OwnsEdge((d.x - b.x) * sgn, (d.y - b.y) * sgn);
            bool own1 = OwnsEdge((a.x - d.x) * sgn, (a.y - d.y) * sgn);
            bool own2 = OwnsEdge((b.x - a.x) * sgn, (b.y - a.y) * sgn);
            for (int y = by0; y <= by1; y++)
            for (int x = bx0; x <= bx1; x++)
            {
                float px = x + 0.5f;
                float py = y + 0.5f;
                float w0 = ((b.x - px) * (d.y - py) - (d.x - px) * (b.y - py)) / area;
                float w1 = ((d.x - px) * (a.y - py) - (a.x - px) * (d.y - py)) / area;
                float w2 = 1f - w0 - w1;
                if (w0 < -1e-6f || w1 < -1e-6f || w2 < -1e-6f) continue;
                if ((w0 <= 1e-6f && !own0) || (w1 <= 1e-6f && !own1) || (w2 <= 1e-6f && !own2)) continue;

                int wx = ((x % n) + n) % n;
                int wy = ((y % n) + n) % n;
                texels.Add(new Texel(wy * n + wx, w0, w1, w2));
            }
        }

        // 正の面積に正規化した辺ベクトルが top（水平で左向き）または left（下向き）なら、その辺上のテクセルを所有する
        private static bool OwnsEdge(float dx, float dy)
        {
            return dy < 0f || (dy == 0f && dx < 0f);
        }

        public static Texture2D CreateTexture(SdfGenerationResult r, bool compressBC7, string name)
        {
            var tex = new Texture2D(r.Size, r.Size, TextureFormat.RGBA32, true, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels(r.Pixels);
            tex.Apply(true, false);
            if (compressBC7)
                EditorUtility.CompressTexture(tex, TextureFormat.BC7, TextureCompressionQuality.Best);

            // Streaming Mip Maps を有効にし、Read/Write を無効にする（アップロードされるバンドルに CPU 側のコピーを残さない）。
            // フラグだけを変えるので、NDMF がアセットに保存するためのピクセルは保持される
            // （Apply(…, makeNoLongerReadable: true) はピクセルを捨てるため使わない）。プレビューも CPU で読まないので同じ
            var so = new SerializedObject(tex);
            var streaming = so.FindProperty("m_StreamingMipmaps");
            if (streaming != null) streaming.boolValue = true;
            var readable = so.FindProperty("m_IsReadable");
            if (readable != null) readable.boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();

            return tex;
        }

        // 空テクセルを周囲の有効テクセルの平均で埋める（バイリニア／ミップの滲み対策）
        private static void Dilate(Color[] pixels, byte[] state, int n, int iterations)
        {
            var newly = new List<(int idx, Color col)>();
            for (int it = 0; it < iterations; it++)
            {
                newly.Clear();
                for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int idx = y * n + x;
                    if (state[idx] != 0) continue;
                    var sum = new Color(0, 0, 0, 0);
                    int k = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int xx = x + dx;
                        int yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= n || yy >= n) continue;
                        int j = yy * n + xx;
                        if (state[j] == 0) continue;
                        sum += pixels[j];
                        k++;
                    }

                    if (k > 0) newly.Add((idx, sum / k));
                }

                if (newly.Count == 0) break;
                foreach (var (idx, col) in newly)
                {
                    pixels[idx] = col;
                    state[idx] = 3;
                }
            }
        }
    }
}
