using System.Collections.Generic;
using UnityEngine;

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    /// <summary>
    /// 生成に使う法線: 位置で溶接した頂点のラプラシアン平滑化。ならす範囲は sample.Triangles（生成対象の三角形）だけ。
    /// 同じ位置でも向きが逆の法線（両面の面の表と裏）は溶接しない。
    /// 開いた縁（溶接後に 1 枚の三角形だけが使う辺）の頂点は、平滑化しても元の法線（溶接した法線の平均）のまま固定する。
    /// 顔と体は首の開いた縁で接するため、縁の法線が内側（あご下など）へ引っぱられると、継ぎ目で両側の明暗がずれる。
    /// Triangles から参照される頂点のみ有効（他は Vector3.zero）。
    /// </summary>
    internal static class SdfProxyNormals
    {
        /// <summary>
        /// 同じ位置の頂点を溶接するのは、溶接済みの法線（和の向き）との内積がこれより大きいときだけ（なす角 120° 未満）。
        /// </summary>
        const float OpposedWeldDot = -0.5f;

        /// <param name="iterations">ならす回数。0 は溶接した頂点の法線の平均を正規化しただけ。</param>
        public static Vector3[] Build(SdfMeshSample sample, int iterations)
        {
            var fPos = sample.Positions;
            var fNrm = sample.Normals;
            var tris = sample.Triangles;
            int count = fPos.Length;
            var result = new Vector3[count];
            if (tris == null || tris.Length == 0) return result;

            var seen = new bool[count];
            var usedList = new List<int>();
            foreach (int i in tris)
            {
                if (seen[i]) continue;
                seen[i] = true;
                usedList.Add(i);
            }

            // 位置で溶接（UV シームで分かれた頂点・ハードエッジを同一視）。位置の格子はワールドで 10 µm
            // （frame の長さ × WorldScale。スケールが不正なら 1 とみなす）。
            // 同じ位置でも、法線がそれまでに溶接した法線の和と逆向き（なす角 120° 超）なら別のグループにする。
            // 表と裏が同じ頂点位置を持つ両面の面で、法線が打ち消し合って 0 になるのを防ぐ（分かれた面の縁は開いた縁として扱われる）
            float scale = sample.WorldScale > 0f && !float.IsInfinity(sample.WorldScale) ? sample.WorldScale : 1f;
            float keyScale = scale * 1e5f;
            var groupOf = new int[count];
            var groups = new Dictionary<Vector3Int, List<int>>();
            var groupNormals = new List<Vector3>();
            foreach (int i in usedList)
            {
                var key = Vector3Int.RoundToInt(fPos[i] * keyScale);
                if (!groups.TryGetValue(key, out var candidates))
                {
                    candidates = new List<int>(1);
                    groups[key] = candidates;
                }

                var dir = fNrm[i].normalized;
                int g = -1;
                float best = OpposedWeldDot;
                foreach (int cand in candidates)
                {
                    float d = Vector3.Dot(groupNormals[cand].normalized, dir);
                    if (d <= best) continue;
                    best = d;
                    g = cand;
                }

                if (g < 0)
                {
                    g = groupNormals.Count;
                    candidates.Add(g);
                    groupNormals.Add(Vector3.zero);
                }

                groupOf[i] = g;
                groupNormals[g] += fNrm[i];
            }

            int gCount = groupNormals.Count;
            var cur = new Vector3[gCount];
            for (int g = 0; g < gCount; g++) cur[g] = groupNormals[g].normalized;

            if (iterations > 0)
            {
                var pinned = OpenBoundaryGroups(tris, groupOf, gCount);
                var neighbors = new HashSet<int>[gCount];
                for (int g = 0; g < gCount; g++) neighbors[g] = new HashSet<int>();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int g0 = groupOf[tris[t]];
                    int g1 = groupOf[tris[t + 1]];
                    int g2 = groupOf[tris[t + 2]];
                    neighbors[g0].Add(g1); neighbors[g0].Add(g2);
                    neighbors[g1].Add(g0); neighbors[g1].Add(g2);
                    neighbors[g2].Add(g0); neighbors[g2].Add(g1);
                }

                var next = new Vector3[gCount];
                for (int it = 0; it < iterations; it++)
                {
                    for (int g = 0; g < gCount; g++)
                    {
                        if (pinned[g])
                        {
                            next[g] = cur[g];
                            continue;
                        }

                        var sum = cur[g];
                        foreach (int nb in neighbors[g]) sum += cur[nb];
                        next[g] = sum.normalized;
                    }

                    (cur, next) = (next, cur);
                }
            }

            foreach (int i in usedList)
                result[i] = cur[groupOf[i]];
            return result;
        }

        /// <summary>
        /// 開いた縁の辺（溶接後の頂点の組で、1 枚の三角形だけが使うもの）に乗る溶接グループ。
        /// 溶接でつぶれた三角形（2 頂点以上が同じグループ）は面を持たないので数えない。
        /// </summary>
        static bool[] OpenBoundaryGroups(int[] tris, int[] groupOf, int gCount)
        {
            var uses = new Dictionary<long, int>();
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int g0 = groupOf[tris[t]];
                int g1 = groupOf[tris[t + 1]];
                int g2 = groupOf[tris[t + 2]];
                if (g0 == g1 || g1 == g2 || g0 == g2) continue;
                CountEdge(uses, g0, g1);
                CountEdge(uses, g1, g2);
                CountEdge(uses, g2, g0);
            }

            var pinned = new bool[gCount];
            foreach (var kv in uses)
            {
                if (kv.Value != 1) continue;
                pinned[(int)(kv.Key >> 32)] = true;
                pinned[(int)(kv.Key & 0xFFFFFFFFL)] = true;
            }

            return pinned;
        }

        static void CountEdge(Dictionary<long, int> uses, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            uses.TryGetValue(key, out int n);
            uses[key] = n + 1;
        }
    }
}
