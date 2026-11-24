using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    internal readonly struct SdfSlot
    {
        public readonly Renderer Renderer;
        public readonly int Slot;
        public readonly Material Material;

        public SdfSlot(Renderer renderer, int slot, Material material)
        {
            Renderer = renderer;
            Slot = slot;
            Material = material;
        }
    }

    internal enum SdfFrameStatus { Ok, Misaligned, HeadRooted }

    internal static class SdfTargetCollector
    {
        const float MisalignThresholdDeg = 30f;

        public static bool IsEligible(Material m)
        {
            if (m == null) return false;
            if (!m.HasProperty("_ShadowMaskType") || !m.HasProperty("_UseShadow")) return false;
            if (IsGemShader(m.shader)) return false;
            return m.GetFloat("_UseShadow") > 0.5f;
        }

        /// <summary>
        /// lilToon の Gem シェーダー（Hidden/lilToonGem・Hidden/lilToonMultiGem など）。影のプロパティはあるが、
        /// 影の処理（LIL_GEM）はコンパイルされない。判定は lilToon の lilShaderUtils.IsGemShaderName と同じ
        /// （最後の '/' より後に "Gem" を含む）。
        /// </summary>
        static bool IsGemShader(Shader shader)
        {
            if (shader == null) return false;
            var name = shader.name;
            var separator = name.LastIndexOf('/');
            if (separator < 0 || separator + 1 == name.Length) return false;
            return name.IndexOf("Gem", separator + 1, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// uvMain のうちツールが再現しない変換（`_MainTex_ScrollRotate` のスクロール・回転、`_ShiftBackfaceUV`）を
        /// 使っていれば true。そのマテリアルには SDF を適用しない（ビルドは警告、プレビューは黙って外す）。
        /// </summary>
        internal static bool HasUnsupportedUvTransform(Material m)
        {
            if (m == null) return false;
            if (m.HasProperty("_MainTex_ScrollRotate"))
            {
                var v = m.GetVector("_MainTex_ScrollRotate");
                if (v.x != 0f || v.y != 0f || v.z != 0f || v.w != 0f) return true;
            }

            return m.HasProperty("_ShiftBackfaceUV") && m.GetFloat("_ShiftBackfaceUV") != 0f;
        }

        public static bool IsTarget(Material m, SdfAutoGenerator c, Func<Material, Material, bool> sameOriginal)
        {
            if (!IsEligible(m)) return false;
            if (c.targetMode == SdfTargetMode.All) return true;
            return c.materials != null && c.materials.Any(x => x != null && sameOriginal(x, m));
        }

        public static List<SdfSlot> CollectRendererSlots(GameObject avatarRoot, SdfAutoGenerator c,
            Func<Material, Material, bool> sameOriginal)
        {
            var result = new List<SdfSlot>();
            foreach (var r in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    if (IsTarget(m, c, sameOriginal)) result.Add(new SdfSlot(r, i, m));
                }
            }
            return result;
        }

        /// <summary>
        /// 基準ボーンの向きを調べる。生成と lilToon はローカル X が水平・+Z が前方であることを前提にするため、
        /// 前方の水平成分がアバター前方から 30° を超えてずれる（ヨー）か、上方向がアバターの上方向から 30° を超えて
        /// 傾いている（ピッチ・ロール）と Misaligned。misalignDeg はその 2 つの角度の大きい方。
        /// </summary>
        public static SdfFrameStatus CheckFrame(Renderer r, Transform avatarRoot, out float misalignDeg)
        {
            var frame = SdfMeshSampler.GetFrame(r);
            var a = Horizontal(frame.forward);
            var b = Horizontal(avatarRoot.forward);
            var yawDeg = a.magnitude < 1e-3f || b.magnitude < 1e-3f ? 90f : Vector3.Angle(a, b);
            var tiltDeg = Vector3.Angle(frame.up, avatarRoot.up);
            misalignDeg = Mathf.Max(yawDeg, tiltDeg);

            if (misalignDeg > MisalignThresholdDeg) return SdfFrameStatus.Misaligned;

            var animator = avatarRoot.GetComponent<Animator>();
            if (animator != null && animator.isHuman && animator.avatar != null)
            {
                var neck = animator.GetBoneTransform(HumanBodyBones.Neck);
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                if (IsSelfOrDescendant(frame, neck) || IsSelfOrDescendant(frame, head))
                    return SdfFrameStatus.HeadRooted;
            }
            return SdfFrameStatus.Ok;
        }

        static bool IsSelfOrDescendant(Transform t, Transform ancestor)
            => ancestor != null && (t == ancestor || t.IsChildOf(ancestor));

        static Vector3 Horizontal(Vector3 v) => new Vector3(v.x, 0f, v.z);

        public static Dictionary<(Renderer, Material), SortedSet<int>> GroupTargets(IEnumerable<SdfSlot> slots)
        {
            var d = new Dictionary<(Renderer, Material), SortedSet<int>>();
            foreach (var s in slots)
            {
                var key = (s.Renderer, s.Material);
                if (!d.TryGetValue(key, out var set)) d[key] = set = new SortedSet<int>();
                set.Add(s.Slot);
            }
            return d;
        }
    }
}
