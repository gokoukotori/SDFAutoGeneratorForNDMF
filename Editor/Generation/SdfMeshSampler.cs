using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    /// <summary>SDF 座標系（基準ボーンのローカル）に変換済みの頂点データ。</summary>
    internal sealed class SdfMeshSample
    {
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector2[] Uvs;
        public int[] Triangles;

        /// <summary>frame（SDF 座標系）の 1 単位がワールドで何 m か。落ち影の届く距離を frame の長さに直すのに使う。</summary>
        public float WorldScale = 1f;
    }

    internal static class SdfMeshSampler
    {
        public static Transform GetFrame(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr && smr.rootBone != null) return smr.rootBone;
            return r.transform;
        }

        /// <summary>
        /// 警告に出す基準ボーン。基準（GetFrame）から親へたどって最初に Renderer の bones に含まれるもの。
        /// MeshRenderer や、見つからないときは基準そのもの。
        /// （MA の FixupHeadChopRootBone が作る "RootBoneSubstitute" のような、bones に無い付け替え先の代わりに元のボーンを出す）
        /// </summary>
        internal static Transform GetDisplayBone(Renderer r)
        {
            var frame = GetFrame(r);
            if (r is SkinnedMeshRenderer smr)
            {
                var bone = FindSkinnedAncestor(smr.bones, frame, out _);
                if (bone != null) return bone;
            }

            return frame;
        }

        /// <summary>from 自身から親へたどり、最初に bones に含まれる Transform とその添字を返す（無ければ null / -1）。</summary>
        static Transform FindSkinnedAncestor(Transform[] bones, Transform from, out int index)
        {
            for (var t = from; t != null; t = t.parent)
            {
                index = Array.IndexOf(bones, t);
                if (index >= 0) return t;
            }

            index = -1;
            return null;
        }

        public static SdfMeshSample Sample(Renderer r, IReadOnlyCollection<int> slots, Vector4 mainTexST)
        {
            var frame = GetFrame(r);
            Mesh mesh;
            Vector3[] pos;
            Vector3[] nrm;

            if (r is SkinnedMeshRenderer smr)
            {
                mesh = smr.sharedMesh;
                if (mesh == null) return Empty();
                int k = -1;
                var ancestorToFrame = Matrix4x4.identity;
                if (smr.rootBone != null)
                {
                    // rootBone が bones に無ければ、bones に含まれる最も近い祖先 A の bindpose を使い、
                    // A → rootBone の相対変換（A のポーズに依存しない）で基準ボーン空間へ移す
                    var ancestor = FindSkinnedAncestor(smr.bones, smr.rootBone, out k);
                    if (ancestor != null && ancestor != smr.rootBone)
                        ancestorToFrame = smr.rootBone.worldToLocalMatrix * ancestor.localToWorldMatrix;
                }

                var bindposes = mesh.bindposes;
                if (k >= 0 && k < bindposes.Length)
                {
                    // ポーズ・ブレンドシェイプ非依存: mesh 空間 -> 基準ボーン空間
                    var m = ancestorToFrame * bindposes[k];
                    var v = mesh.vertices;
                    var n = mesh.normals;
                    pos = new Vector3[v.Length];
                    nrm = new Vector3[v.Length];
                    for (int i = 0; i < v.Length; i++)
                    {
                        pos[i] = m.MultiplyPoint3x4(v[i]);
                        nrm[i] = i < n.Length ? m.MultiplyVector(n[i]).normalized : Vector3.zero;
                    }
                }
                else
                {
                    var baked = new Mesh();
                    try
                    {
                        smr.BakeMesh(baked, true);
                        var v = baked.vertices;
                        var n = baked.normals;
                        var t = smr.transform;
                        var rot = t.rotation;
                        var origin = t.position;
                        pos = new Vector3[v.Length];
                        nrm = new Vector3[v.Length];
                        for (int i = 0; i < v.Length; i++)
                        {
                            // BakeMesh(..., true) はレンダラーのスケール適用済みのローカル座標
                            pos[i] = frame.InverseTransformPoint(origin + rot * v[i]);
                            nrm[i] = i < n.Length
                                ? frame.InverseTransformDirection(rot * n[i]).normalized
                                : Vector3.zero;
                        }
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(baked);
                    }
                }
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) return Empty();
                pos = mesh.vertices;
                var n = mesh.normals;
                nrm = new Vector3[pos.Length];
                for (int i = 0; i < pos.Length; i++)
                    nrm[i] = i < n.Length ? n[i].normalized : Vector3.zero;
            }

            var srcUv = mesh.uv;
            var uvs = new Vector2[pos.Length];
            for (int i = 0; i < uvs.Length; i++)
            {
                var uv = i < srcUv.Length ? srcUv[i] : Vector2.zero;
                uvs[i] = new Vector2(uv.x * mainTexST.x + mainTexST.z, uv.y * mainTexST.y + mainTexST.w);
            }

            // スロットがサブメッシュ数を超えるときは最後のサブメッシュ（Unity の描画と同じ）。
            // サブメッシュが無い・三角形以外（点・線・四角形）のサブメッシュは使わない（描画範囲なしとして扱われる）
            var tris = new List<int>();
            int subCount = mesh.subMeshCount;
            foreach (int slot in slots)
            {
                if (subCount == 0) break;
                int sub = Mathf.Clamp(slot, 0, subCount - 1);
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                tris.AddRange(mesh.GetTriangles(sub));
            }

            return new SdfMeshSample
            {
                Positions = pos, Normals = nrm, Uvs = uvs, Triangles = tris.ToArray(), WorldScale = MeanScale(frame),
            };
        }

        /// <summary>frame の lossyScale の 3 成分（絶対値）の平均。</summary>
        static float MeanScale(Transform frame)
        {
            var s = frame.lossyScale;
            return (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
        }

        private static SdfMeshSample Empty()
        {
            return new SdfMeshSample
            {
                Positions = new Vector3[0],
                Normals = new Vector3[0],
                Uvs = new Vector2[0],
                Triangles = new int[0],
            };
        }
    }
}
