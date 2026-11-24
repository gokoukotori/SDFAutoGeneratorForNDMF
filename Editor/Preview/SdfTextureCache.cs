using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    /// <summary>
    /// プレビュー用 SDF テクスチャの参照カウント付きキャッシュ。テクスチャはこのキャッシュが所有する（HideFlags.HideAndDontSave）。
    /// 件数が 32 を超えたら、使われていない（参照 0 の）ものを最後に取得された時刻の古い順に破棄する。
    /// 使用中のものは破棄しないので、すべて使用中なら一時的に 32 を超える。ドメインリロードの前に全件破棄する。
    /// </summary>
    [InitializeOnLoad]
    internal static class SdfTextureCache
    {
        internal const int MaxEntries = 32;

        sealed class Entry
        {
            public readonly string Key;
            public Texture2D Texture;
            public int RefCount;

            public Entry(string key)
            {
                Key = key;
            }
        }

        // 先頭が最も古い（最後に Acquire されたのが最も前）
        static readonly LinkedList<Entry> Order = new LinkedList<Entry>();
        static readonly Dictionary<string, LinkedListNode<Entry>> Map = new Dictionary<string, LinkedListNode<Entry>>();

        static SdfTextureCache()
        {
            // リロード後は辞書が空になり、残ったテクスチャ・プレビューの複製は誰も破棄できなくなるため先に破棄する
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
        }

        /// <summary>キーのテクスチャを返し、参照を 1 つ増やす（無ければ create で作る）。使い終わったら <see cref="Release"/> を呼ぶ。</summary>
        public static Texture2D Acquire(string key, Func<Texture2D> create)
        {
            Map.TryGetValue(key, out var node);
            if (node == null || node.Value.Texture == null)
            {
                var tex = create();
                if (tex == null) return null;
                tex.hideFlags = HideFlags.HideAndDontSave;
                if (node == null)
                {
                    node = new LinkedListNode<Entry>(new Entry(key));
                    Map[key] = node;
                }

                // 外部で破棄されていた場合は、既存の参照数を引き継いだままテクスチャだけ作り直す
                node.Value.Texture = tex;
            }

            node.Value.RefCount++;
            if (node.List != null) Order.Remove(node);
            Order.AddLast(node);
            Trim();
            return node.Value.Texture;
        }

        /// <summary><see cref="Acquire"/> で増やした参照を 1 つ返す。</summary>
        public static void Release(string key)
        {
            if (!Map.TryGetValue(key, out var node)) return;
            if (node.Value.RefCount > 0) node.Value.RefCount--;
            Trim();
        }

        /// <summary>スクリプトの再読み込みの前に、プレビューの複製マテリアルとキャッシュのテクスチャをすべて破棄する。</summary>
        internal static void BeforeAssemblyReload()
        {
            SdfPreviewFilter.DestroyAllClones();
            Clear();
        }

        /// <summary>使用中かどうかに関係なく全件破棄する（ドメインリロード前・テスト用）。</summary>
        public static void Clear()
        {
            foreach (var e in Order)
            {
                if (e.Texture != null) Object.DestroyImmediate(e.Texture);
            }

            Order.Clear();
            Map.Clear();
        }

        static void Trim()
        {
            var node = Order.First;
            while (Order.Count > MaxEntries && node != null)
            {
                var next = node.Next;
                if (node.Value.RefCount == 0)
                {
                    Order.Remove(node);
                    Map.Remove(node.Value.Key);
                    if (node.Value.Texture != null) Object.DestroyImmediate(node.Value.Texture);
                }

                node = next;
            }
        }
    }
}
