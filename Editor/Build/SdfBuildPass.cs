using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    /// <summary>
    /// ビルド時に対象の (Renderer, マテリアル) ごとに SDF を生成し、複製したマテリアルを Renderer スロットと
    /// アニメーションの m_Materials カーブへ差し替える。MA の後に動くため、MA Material Setter / Swap は
    /// この時点でカーブ（または静的なスロット）に変換済みで、フィールドの書き換えは不要。
    /// </summary>
    [DependsOnContext(typeof(AnimatorServicesContext))]
    internal class SdfBuildPass : Pass<SdfBuildPass>
    {
        /// <summary>README「圧縮の検証結果」により BC7 を採用。</summary>
        internal const bool CompressBC7 = true;

        const float MirroredWarnRatio = 0.3f;

        static readonly Regex MaterialSlotProperty = new Regex(@"^m_Materials\.Array\.data\[(\d+)\]$");

        public override string DisplayName => "SDF 影マスクの生成";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            var all = root.GetComponentsInChildren<SdfAutoGenerator>(true);
            try
            {
                Process(context, root, all);
            }
            finally
            {
                foreach (var c in all)
                {
                    if (c != null) Object.DestroyImmediate(c);
                }
            }
        }

        static void Process(BuildContext context, GameObject root, SdfAutoGenerator[] all)
        {
            // ① ルート以外のものは無視して警告
            SdfAutoGenerator c = null;
            foreach (var comp in all)
            {
                if (comp.transform == root.transform) c = comp;
                else SdfMessages.Warn("NonRoot", comp.gameObject.name, comp.gameObject);
            }

            // ② ルートのものが無い・無効なら何もしない（コンポーネントの削除は Execute の finally）
            if (c == null || !c.enabled) return;

            var animator = context.Extension<AnimatorServicesContext>();
            var index = animator.AnimationIndex;
            var curveRenderers = new CurveRendererResolver(root, animator.ObjectPathRemapper);

            // ③ Renderer スロットとカーブから対象を集める
            var slots = SdfTargetCollector.CollectRendererSlots(root, c, SameOriginal);
            var anyTarget = slots.Count > 0;
            var missingWarned = new HashSet<(string, Material)>();
            foreach (var (binding, obj) in index.GetPPtrReferencedObjectsWithBinding.ToList())
            {
                if (!(obj is Material m)) continue;
                if (!TryParseMaterialSlot(binding, out var slot)) continue;
                if (!SdfTargetCollector.IsTarget(m, c, SameOriginal)) continue;
                anyTarget = true;

                var r = curveRenderers.Resolve(binding);
                if (r == null)
                {
                    // ユーザーが直せない（他ツールが作ったアニメーションなど）ため、NDMF のレポートではなくコンソールにだけ出す
                    if (missingWarned.Add((binding.path, m)))
                        SdfMessages.LogWarning("CurveRendererMissing", root, OriginalName(m), binding.path);
                    continue;
                }

                slots.Add(new SdfSlot(r, slot, m));
            }

            // ⑦ 対象のマテリアルが 1 つも見つからない
            if (!anyTarget)
            {
                SdfMessages.Warn("NoTargets");
                return;
            }

            // ④ 座標系のチェック（Renderer ごとに 1 回。スロットとカーブの両方に効く）
            var excluded = new HashSet<Renderer>();
            foreach (var r in slots.Select(s => s.Renderer).Distinct().ToList())
            {
                var status = SdfTargetCollector.CheckFrame(r, root.transform, out var misalignDeg);
                // 表示名は bones に含まれる最も近い祖先（MA が付け替えた "RootBoneSubstitute" ではなく元のボーン）
                var boneName = SdfMeshSampler.GetDisplayBone(r).name;
                if (status == SdfFrameStatus.Misaligned)
                {
                    excluded.Add(r);
                    SdfMessages.Warn("FrameMisaligned", r.gameObject.name, boneName,
                        misalignDeg.ToString("F1", CultureInfo.InvariantCulture), r);
                }
                else if (status == SdfFrameStatus.HeadRooted)
                {
                    SdfMessages.Warn("HeadRooted", r.gameObject.name, boneName, r);
                }
            }

            // ⑤ (Renderer, マテリアル) ごとに 1 枚生成して複製を作る
            var groups = SdfTargetCollector.GroupTargets(slots.Where(s => !excluded.Contains(s.Renderer)));
            var clones = new Dictionary<(Renderer, Material), Material>();
            var p = new SdfGenerationParams((int)c.resolution, c.lightElevation, c.normalSmoothing, c.shadowDistance);
            foreach (var group in groups)
            {
                var (r, source) = group.Key;
                var sourceName = OriginalName(source);

                // uvMain のスクロール・回転・裏面シフトは再現しないため、そのマテリアルには適用しない
                if (SdfTargetCollector.HasUnsupportedUvTransform(source))
                {
                    SdfMessages.Warn("UnsupportedUvTransform", r.gameObject.name, sourceName, r);
                    continue;
                }

                SdfGenerationResult result;
                try
                {
                    var sample = SdfMeshSampler.Sample(r, group.Value, MainTexST(source));
                    result = SdfTextureGenerator.Generate(sample, p);
                }
                catch (Exception e)
                {
                    // 極端な UV・GPU の失敗などで生成できなければ、このマテリアルだけ適用せずにほかを続ける。
                    // 調査用に、元の例外（スタックトレース付き）もコンソールに出す
                    Debug.LogException(e);
                    SdfMessages.Warn("GenerationFailed", r.gameObject.name, sourceName, e.Message, r);
                    continue;
                }

                // UV0 が無い・UV の三角形がすべてつぶれているなど、1 テクセルも描けなければ適用しない
                if (result.Covered == 0)
                {
                    SdfMessages.Warn("NoUvCoverage", r.gameObject.name, sourceName, r);
                    continue;
                }

                if (result.MirroredRatio > MirroredWarnRatio)
                {
                    SdfMessages.Warn("MirroredRatio", r.gameObject.name, sourceName,
                        Mathf.RoundToInt(result.MirroredRatio * 100f), r);
                }

                var tex = SdfTextureGenerator.CreateTexture(result, CompressBC7, source.name + "_SDF");
                var clone = SdfMaterialWriter.CreateSdfMaterial(source, tex);
                ObjectRegistry.RegisterReplacedObject(source, clone);
                clones[group.Key] = clone;
            }

            if (clones.Count == 0) return;

            // ⑥ Renderer スロットはマテリアルの同一性で差し替える（カーブ由来のスロット番号は生成の範囲にだけ使う）
            foreach (var r in clones.Keys.Select(k => k.Item1).Distinct())
            {
                var mats = r.sharedMaterials;
                var changed = false;
                for (var i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && clones.TryGetValue((r, mats[i]), out var clone))
                    {
                        mats[i] = clone;
                        changed = true;
                    }
                }

                if (changed) r.sharedMaterials = mats;
            }

            // カーブはバインディングの Renderer とマテリアルの組で差し替える
            index.RewriteObjectCurves((binding, obj) =>
            {
                if (!(obj is Material m)) return obj;
                if (!TryParseMaterialSlot(binding, out _)) return obj;
                var r = curveRenderers.Resolve(binding);
                if (r == null) return obj;
                return clones.TryGetValue((r, m), out var clone) ? clone : obj;
            });
        }

        /// <summary>
        /// 警告に出すマテリアル名。AOME などがビルド中に作った複製（ObjectRegistry に登録済み）なら元のマテリアルの名前。
        /// 複製の上に GetReference を呼ぶとその後の RegisterReplacedObject が失敗するため、このツールの複製には使わない。
        /// </summary>
        internal static string OriginalName(Material m)
        {
            var original = ObjectRegistry.GetReference(m)?.Object as Material;
            return original != null ? original.name : m.name;
        }

        static bool SameOriginal(Material a, Material b)
        {
            if (a == null || b == null) return false;
            return ObjectRegistry.GetReference(a).Equals(ObjectRegistry.GetReference(b));
        }

        /// <summary>uvMain に使う `_MainTex` の ST（プレビューと共通）。</summary>
        internal static Vector4 MainTexST(Material m)
        {
            if (!m.HasProperty("_MainTex")) return new Vector4(1f, 1f, 0f, 0f);
            var s = m.GetTextureScale("_MainTex");
            var o = m.GetTextureOffset("_MainTex");
            return new Vector4(s.x, s.y, o.x, o.y);
        }

        /// <summary>SkinnedMeshRenderer / MeshRenderer（または Renderer）の m_Materials.Array.data[i] なら true。</summary>
        static bool TryParseMaterialSlot(EditorCurveBinding binding, out int slot)
        {
            slot = -1;
            var t = binding.type;
            if (t != typeof(SkinnedMeshRenderer) && t != typeof(MeshRenderer) && t != typeof(Renderer)) return false;
            var match = MaterialSlotProperty.Match(binding.propertyName);
            return match.Success && int.TryParse(match.Groups[1].Value, out slot);
        }

        /// <summary>カーブのパス（アバタールート基準の仮想パス）から対象 Renderer を引く。</summary>
        sealed class CurveRendererResolver
        {
            readonly GameObject _root;
            readonly ObjectPathRemapper _remapper;
            readonly Dictionary<(string, Type), Renderer> _cache = new Dictionary<(string, Type), Renderer>();

            public CurveRendererResolver(GameObject root, ObjectPathRemapper remapper)
            {
                _root = root;
                _remapper = remapper;
            }

            public Renderer Resolve(EditorCurveBinding binding)
            {
                var key = (binding.path, binding.type);
                if (_cache.TryGetValue(key, out var cached)) return cached;

                var go = string.IsNullOrEmpty(binding.path) ? _root : _remapper.GetObjectForPath(binding.path);
                Renderer r = null;
                if (go != null)
                {
                    var comp = go.GetComponent(binding.type) as Renderer;
                    if (comp is SkinnedMeshRenderer || comp is MeshRenderer) r = comp;
                }

                _cache[key] = r;
                return r;
            }
        }
    }
}
