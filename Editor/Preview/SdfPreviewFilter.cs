using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    /// <summary>
    /// NDMF プレビュー。アバタールートの <see cref="SdfAutoGenerator"/> の対象になりうるスロット（元のマテリアルが適用条件を
    /// 満たすもの）を持つ Renderer ごとにグループを作り、元またはプロキシ上のマテリアルが対象なら、プロキシ上のマテリアル
    /// （前段のフィルタ適用後）を複製して 512 の SDF を設定する。元の Renderer とマテリアルは変えない。
    /// </summary>
    internal sealed class SdfPreviewFilter : IRenderFilter
    {
        internal const int PreviewResolution = 512;

        // 生きているプレビュー用の複製（HideAndDontSave）。スクリプトの再読み込みでノードが Dispose されずに消えても残らないよう、
        // 再読み込みの前にまとめて破棄する（SdfTextureCache.BeforeAssemblyReload）
        static readonly HashSet<Material> LiveClones = new HashSet<Material>(MaterialReferenceComparer.Instance);

        /// <summary>生きているプレビュー用の複製をすべて破棄する（スクリプトの再読み込み前）。</summary>
        internal static void DestroyAllClones()
        {
            foreach (var m in LiveClones)
            {
                if (m != null) Object.DestroyImmediate(m);
            }

            LiveClones.Clear();
        }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var groups = ImmutableList.CreateBuilder<RenderGroup>();
            foreach (var root in context.GetAvatarRoots())
            {
                if (root == null) continue;
                var settings = ObserveSettings(context, root);
                if (settings == null) continue;

                if (!settings.MayHaveMembers) continue;

                foreach (var r in context.GetComponentsInChildren<Renderer>(root, true))
                {
                    if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;

                    // 候補のスロットは元の Renderer のマテリアルが適用条件（_UseShadow など）を満たすもの（プレビュー中は
                    // ObjectRegistry に頼らない）。「指定」で一覧に入っているかは、元とプロキシ上のマテリアル（MA Material Swap の
                    // プレビューなど）の両方を見てノードがスロットごとに決める（裁定 14）。条件の変化も監視してグループを作り直す
                    var mats = context.Observe(r, x => x.sharedMaterials, (a, b) => a.SequenceEqual(b));
                    var slots = new List<int>();
                    for (var i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;
                        if (!context.Observe(m, SdfTargetCollector.IsEligible, (a, b) => a == b)) continue;
                        slots.Add(i);
                    }

                    if (slots.Count == 0) continue;
                    var data = new SdfPreviewGroupData(settings, slots.ToArray());
                    groups.Add(RenderGroup.For(r).WithData(data, EqualityComparer<SdfPreviewGroupData>.Default));
                }
            }

            return groups.ToImmutable();
        }

        /// <summary>ルートのコンポーネントだけを見る。無い・無効・ルートが EditorOnly なら null。</summary>
        static SdfPreviewSettings ObserveSettings(ComputeContext context, GameObject root)
        {
            if (context.Observe(root, g => g.CompareTag("EditorOnly"), (a, b) => a == b)) return null;
            var c = context.GetComponent<SdfAutoGenerator>(root);
            if (c == null) return null;
            var s = context.Observe(c, SdfPreviewSettings.From, (a, b) => a == null ? b == null : a.Equals(b));
            return s != null && s.Enabled ? s : null;
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            var data = group.GetData<SdfPreviewGroupData>();
            var pairs = proxyPairs.ToList();
            return Task.FromResult<IRenderFilterNode>(Node.Create(data, pairs, Node.ObserveInputs(context, pairs)));
        }

        sealed class Node : IRenderFilterNode
        {
            readonly SdfPreviewGroupData _data;

            // 生成に使う元の Renderer の入力（メッシュ・基準ボーン）。NDMF は基準ボーンの変化を Shapes としか伝えないため自分で比べる
            Dictionary<Renderer, (Mesh Mesh, Transform RootBone)> _inputs;

            // 取得したテクスチャのキャッシュキー（Dispose で参照を返す）
            readonly List<string> _textureKeys = new List<string>();

            // 元の Renderer → (スロット, 差し替えるマテリアル)
            readonly Dictionary<Renderer, (int Slot, Material Material)[]> _replacements =
                new Dictionary<Renderer, (int Slot, Material Material)[]>();

            readonly List<Material> _created = new List<Material>();

            public RenderAspects WhatChanged { get; private set; } = RenderAspects.Material | RenderAspects.Texture;

            Node(SdfPreviewGroupData data)
            {
                _data = data;
            }

            /// <summary>元の Renderer のメッシュと基準ボーンを context で監視し、その値を返す。</summary>
            public static Dictionary<Renderer, (Mesh Mesh, Transform RootBone)> ObserveInputs(ComputeContext context,
                IEnumerable<(Renderer, Renderer)> proxyPairs)
            {
                var inputs = new Dictionary<Renderer, (Mesh Mesh, Transform RootBone)>();
                foreach (var (original, _) in proxyPairs)
                {
                    if (original == null) continue;
                    if (original is SkinnedMeshRenderer smr)
                    {
                        inputs[original] = context.Observe(smr,
                            s => s == null ? default : (s.sharedMesh, s.rootBone),
                            (a, b) => a.Item1 == b.Item1 && a.Item2 == b.Item2);
                    }
                    else
                    {
                        var mf = context.GetComponent<MeshFilter>(original.gameObject);
                        var mesh = mf != null
                            ? context.Observe(mf, f => f == null ? null : f.sharedMesh, (a, b) => a == b)
                            : null;
                        inputs[original] = (mesh, null);
                    }
                }

                return inputs;
            }

            static bool SameInputs(Dictionary<Renderer, (Mesh Mesh, Transform RootBone)> a,
                Dictionary<Renderer, (Mesh Mesh, Transform RootBone)> b)
            {
                if (a.Count != b.Count) return false;
                foreach (var kv in a)
                {
                    if (!b.TryGetValue(kv.Key, out var other)) return false;
                    if (kv.Value.Mesh != other.Mesh || kv.Value.RootBone != other.RootBone) return false;
                }

                return true;
            }

            public static Node Create(SdfPreviewGroupData data, IReadOnlyList<(Renderer, Renderer)> proxyPairs,
                Dictionary<Renderer, (Mesh Mesh, Transform RootBone)> inputs)
            {
                var node = new Node(data) { _inputs = inputs };
                var settings = data.Settings;
                if (settings.Component == null || settings.Root == null) return node;

                try
                {
                    node.Build(proxyPairs);
                }
                catch
                {
                    // 途中までに作った複製を残さない（例外は NDMF がログに出す）
                    node.Dispose();
                    throw;
                }

                return node;
            }

            void Build(IEnumerable<(Renderer, Renderer)> proxyPairs)
            {
                var settings = _data.Settings;
                foreach (var (original, proxy) in proxyPairs)
                {
                    if (original == null || proxy == null) continue;
                    // 向きのずれた Renderer はビルドと同じく対象外（警告はビルド時だけ）。HeadRooted はそのまま処理する
                    if (SdfTargetCollector.CheckFrame(original, settings.Root, out _) == SdfFrameStatus.Misaligned)
                        continue;

                    // 一覧に入っているかは元とプロキシ上のマテリアルのどちらかで決める（裁定 14）。
                    // 適用条件と生成に使う値はプロキシ上のマテリアル（前段のフィルタが差し替えたもの）から取る
                    var originalMats = original.sharedMaterials;
                    var proxyMats = proxy.sharedMaterials;
                    var bySource = new Dictionary<Material, SortedSet<int>>();
                    foreach (var slot in _data.Slots)
                    {
                        if (slot >= proxyMats.Length) continue;
                        var m = proxyMats[slot];
                        var originalMat = slot < originalMats.Length ? originalMats[slot] : null;
                        if (!settings.IsMember(originalMat) && !settings.IsMember(m)) continue;
                        if (!SdfTargetCollector.IsEligible(m)) continue;
                        // ビルドで警告して外すもの（UV のスクロール・回転・裏面シフト）はプレビューでは黙って外す
                        if (SdfTargetCollector.HasUnsupportedUvTransform(m)) continue;
                        if (!bySource.TryGetValue(m, out var set)) bySource[m] = set = new SortedSet<int>();
                        set.Add(slot);
                    }

                    var reps = new List<(int, Material)>();
                    foreach (var kv in bySource)
                    {
                        var clone = CreateSdfMaterial(original, kv.Key, kv.Value);
                        if (clone == null) continue;
                        foreach (var slot in kv.Value) reps.Add((slot, clone));
                    }

                    if (reps.Count > 0) _replacements[original] = reps.ToArray();
                }
            }

            Material CreateSdfMaterial(Renderer original, Material source, SortedSet<int> slots)
            {
                var mesh = SharedMesh(original);
                if (mesh == null) return null;

                var st = SdfBuildPass.MainTexST(source);
                var settings = _data.Settings;
                var p = new SdfGenerationParams(PreviewResolution, settings.LightElevation, settings.NormalSmoothing,
                    settings.ShadowDistance);
                Texture2D tex;
                string key;
                try
                {
                    // メッシュをその場で書き換えても（再インポートなど）InstanceID は変わらないため、
                    // 毎回サンプリングして中身のハッシュをキーに入れる。キャッシュするのは生成だけ
                    var sample = SdfMeshSampler.Sample(original, slots, st);
                    key = CacheKey(mesh, SdfMeshSampler.GetFrame(original), slots, st, p, SampleHash(sample));
                    tex = SdfTextureCache.Acquire(key, () =>
                    {
                        var result = SdfTextureGenerator.Generate(sample, p);
                        // UV に描画範囲が無ければビルドと同じく適用しない（警告はビルド時だけ。null はキャッシュされない）
                        if (result.Covered == 0) return null;
                        return SdfTextureGenerator.CreateTexture(result, false, source.name + "_SDF_Preview");
                    });
                }
                catch (Exception e)
                {
                    // 極端な UV などで生成できなければ、このスロットだけ適用しない（ビルドと同じ。プレビューはコンソールにだけ出す）
                    SdfMessages.LogWarning("GenerationFailed", original, original.gameObject.name, source.name, e.Message);
                    return null;
                }

                if (tex == null) return null;
                _textureKeys.Add(key);

                var clone = SdfMaterialWriter.CreateSdfMaterial(source, tex);
                clone.hideFlags = HideFlags.HideAndDontSave;
                _created.Add(clone);
                LiveClones.Add(clone);
                return clone;
            }

            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> proxyPairs,
                ComputeContext context, RenderAspects updatedAspects)
            {
                // 新しい context でも監視を続ける（this を返す場合も同じ）
                var pairs = proxyPairs.ToList();
                var inputs = ObserveInputs(context, pairs);

                // マテリアル・メッシュが変わらず、メッシュと基準ボーンも同じなら作り直さない
                const RenderAspects rebuildOn = RenderAspects.Material | RenderAspects.Mesh;
                if ((updatedAspects & rebuildOn) == 0 && SameInputs(inputs, _inputs))
                {
                    WhatChanged = 0;
                    return Task.FromResult<IRenderFilterNode>(this);
                }

                return Task.FromResult<IRenderFilterNode>(Create(_data, pairs, inputs));
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (original == null || proxy == null) return;
                if (!_replacements.TryGetValue(original, out var reps)) return;

                var mats = proxy.sharedMaterials;
                var changed = false;
                foreach (var (slot, m) in reps)
                {
                    if (slot >= mats.Length || m == null || ReferenceEquals(mats[slot], m)) continue;
                    mats[slot] = m;
                    changed = true;
                }

                if (changed) proxy.sharedMaterials = mats;
            }

            /// <summary>
            /// 自分が作った複製だけを破棄し、テクスチャの参照を返す（前段のマテリアルには触れない。テクスチャはキャッシュが破棄する）。
            /// 2 回呼んでも安全。
            /// </summary>
            public void Dispose()
            {
                foreach (var m in _created)
                {
                    LiveClones.Remove(m);
                    if (m != null) Object.DestroyImmediate(m);
                }

                foreach (var key in _textureKeys) SdfTextureCache.Release(key);

                _created.Clear();
                _textureKeys.Clear();
                _replacements.Clear();
            }
        }

        static Mesh SharedMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        /// <summary>
        /// メッシュ・基準ボーン・スロット集合・ST・生成の設定 3 値（光の高さ・法線のなめらかさ・落ち影の届く距離）・解像度・
        /// サンプルの中身のハッシュで決まるキー。
        /// マテリアルの影の設定は生成に使わないので入れない（影の境界だけ違うマテリアルは同じテクスチャを使う）。
        /// </summary>
        internal static string CacheKey(Mesh mesh, Transform frame, IEnumerable<int> slots, Vector4 st,
            in SdfGenerationParams p, ulong sampleHash)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(mesh.GetInstanceID()).Append('|');
            sb.Append(frame != null ? frame.GetInstanceID() : 0).Append('|');
            sb.Append(string.Join(",", slots.OrderBy(s => s))).Append('|');
            sb.Append(st.x.ToString("R", inv)).Append(',').Append(st.y.ToString("R", inv)).Append(',')
                .Append(st.z.ToString("R", inv)).Append(',').Append(st.w.ToString("R", inv)).Append('|');
            sb.Append(p.LightElevationDeg.ToString("R", inv)).Append(',').Append(p.SmoothIterations.ToString(inv))
                .Append(',').Append(p.ShadowDistanceMeters.ToString("R", inv)).Append('|');
            sb.Append(p.Size.ToString(inv)).Append('|');
            sb.Append(sampleHash.ToString("x16", inv));
            return sb.ToString();
        }

        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        /// <summary>
        /// サンプル（位置・法線・UV・三角形・ワールドのスケール）の中身の 64 ビット FNV-1a ハッシュ。float はビット列のまま使う。
        /// スケールは落ち影の届く距離を frame の長さに直すのに使うので入れる（位置は基準ボーンのスケールで変わらない）。
        /// </summary>
        internal static ulong SampleHash(SdfMeshSample s)
        {
            var h = FnvOffset;
            h = Mix(h, Bits(s.WorldScale));
            h = Mix(h, (uint)s.Positions.Length);
            foreach (var v in s.Positions) h = Mix(Mix(Mix(h, Bits(v.x)), Bits(v.y)), Bits(v.z));
            h = Mix(h, (uint)s.Normals.Length);
            foreach (var v in s.Normals) h = Mix(Mix(Mix(h, Bits(v.x)), Bits(v.y)), Bits(v.z));
            h = Mix(h, (uint)s.Uvs.Length);
            foreach (var v in s.Uvs) h = Mix(Mix(h, Bits(v.x)), Bits(v.y));
            h = Mix(h, (uint)s.Triangles.Length);
            foreach (var t in s.Triangles) h = Mix(h, (uint)t);
            return h;
        }

        /// <summary>32 ビット値を下位バイトから 1 バイトずつ FNV-1a で混ぜる。</summary>
        static ulong Mix(ulong h, uint word)
        {
            h = (h ^ (word & 0xFF)) * FnvPrime;
            h = (h ^ ((word >> 8) & 0xFF)) * FnvPrime;
            h = (h ^ ((word >> 16) & 0xFF)) * FnvPrime;
            h = (h ^ (word >> 24)) * FnvPrime;
            return h;
        }

        static uint Bits(float f) => new FloatBits { Float = f }.UInt;

        [StructLayout(LayoutKind.Explicit)]
        struct FloatBits
        {
            [FieldOffset(0)] public float Float;
            [FieldOffset(0)] public uint UInt;
        }
    }

    /// <summary>プレビューに使うコンポーネント設定のスナップショット（値で比較する）。</summary>
    internal sealed class SdfPreviewSettings : IEquatable<SdfPreviewSettings>
    {
        public readonly SdfAutoGenerator Component;
        public readonly Transform Root;
        public readonly bool Enabled;
        public readonly SdfTargetMode TargetMode;
        public readonly Material[] Materials;
        public readonly float LightElevation;
        public readonly int NormalSmoothing;
        public readonly float ShadowDistance;

        SdfPreviewSettings(SdfAutoGenerator c)
        {
            Component = c;
            Root = c.transform;
            Enabled = c.enabled;
            TargetMode = c.targetMode;
            Materials = c.materials != null ? c.materials.ToArray() : Array.Empty<Material>();
            LightElevation = c.lightElevation;
            NormalSmoothing = c.normalSmoothing;
            ShadowDistance = c.shadowDistance;
        }

        public static SdfPreviewSettings From(SdfAutoGenerator c) => c == null ? null : new SdfPreviewSettings(c);

        /// <summary>
        /// 対象に含まれるか（一覧と参照で照合する。ノードは元とプロキシ上のマテリアルの両方で呼ぶ。
        /// lilToon の適用条件はプロキシ側で判定する）。
        /// </summary>
        public bool IsMember(Material m)
        {
            if (m == null) return false;
            return TargetMode == SdfTargetMode.All || Array.Exists(Materials, x => ReferenceEquals(x, m));
        }

        /// <summary>「すべて」か、「指定」で一覧に 1 つ以上マテリアルがある。</summary>
        public bool MayHaveMembers => TargetMode == SdfTargetMode.All || Array.Exists(Materials, x => x != null);

        public bool Equals(SdfPreviewSettings other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return ReferenceEquals(Component, other.Component)
                   && ReferenceEquals(Root, other.Root)
                   && Enabled == other.Enabled
                   && TargetMode == other.TargetMode
                   && Materials.SequenceEqual(other.Materials, MaterialReferenceComparer.Instance)
                   && LightElevation.Equals(other.LightElevation)
                   && NormalSmoothing == other.NormalSmoothing
                   && ShadowDistance.Equals(other.ShadowDistance);
        }

        public override bool Equals(object obj) => Equals(obj as SdfPreviewSettings);

        public override int GetHashCode()
        {
            return HashCode.Combine(Component != null ? Component.GetInstanceID() : 0, (int)TargetMode, Enabled,
                Materials.Length, LightElevation, NormalSmoothing, ShadowDistance);
        }
    }

    sealed class MaterialReferenceComparer : IEqualityComparer<Material>
    {
        public static readonly MaterialReferenceComparer Instance = new MaterialReferenceComparer();

        public bool Equals(Material a, Material b) => ReferenceEquals(a, b);

        public int GetHashCode(Material m) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(m);
    }

    /// <summary>RenderGroup に付けるデータ: 設定と、元の Renderer で候補になったスロット（適用条件を満たすもの）。</summary>
    internal sealed class SdfPreviewGroupData : IEquatable<SdfPreviewGroupData>
    {
        public readonly SdfPreviewSettings Settings;
        public readonly int[] Slots;

        public SdfPreviewGroupData(SdfPreviewSettings settings, int[] slots)
        {
            Settings = settings;
            Slots = slots;
        }

        public bool Equals(SdfPreviewGroupData other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Settings.Equals(other.Settings) && Slots.SequenceEqual(other.Slots);
        }

        public override bool Equals(object obj) => Equals(obj as SdfPreviewGroupData);

        public override int GetHashCode()
        {
            var h = Settings.GetHashCode();
            foreach (var s in Slots) h = HashCode.Combine(h, s);
            return h;
        }
    }
}
