using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    /// <summary>ビルド時の警告（NDMF ErrorReport、日本語）。</summary>
    internal static class SdfMessages
    {
        static readonly Dictionary<string, string> Ja = new Dictionary<string, string>
        {
            ["NonRoot"] =
                "SDF Auto Generator はアバタールートに置いたものだけが有効です。「{0}」のコンポーネントは無視しました。",
            ["NoTargets"] =
                "SDF を適用する対象のマテリアルがありません（lilToon で影が有効なものだけが対象です）。",
            ["FrameMisaligned"] =
                "「{0}」は基準ボーン「{1}」の前方がアバターの前方から {2}° ずれているため、SDF を適用しませんでした。",
            ["HeadRooted"] =
                "「{0}」の基準ボーン「{1}」は首・頭側にあるため、このメッシュの影は頭の向きに追従し、体と基準が異なります。",
            ["MirroredRatio"] =
                "「{0}」のマテリアル「{1}」は UV の {2}% が左右で共有されているため、その部分は通常の影になります。",
            ["NoUvCoverage"] =
                "「{0}」のマテリアル「{1}」は UV に描画範囲がないため、SDF を適用しませんでした。",
            ["UnsupportedUvTransform"] =
                "「{0}」のマテリアル「{1}」はメインテクスチャの UV スクロール・回転・裏面 UV シフトを使っているため、SDF を適用しませんでした。",
            ["GenerationFailed"] =
                "「{0}」のマテリアル「{1}」は SDF を生成できなかったため、適用しませんでした（{2}）。",
            // コンソールだけに出す（LogWarning）。ユーザーが直せないため NDMF のレポートには出さない
            ["CurveRendererMissing"] =
                "アニメーションで差し替えられるマテリアル「{0}」の対象 Renderer（{1}）が見つからないため、SDF を適用しませんでした。",
        };

        public static readonly Localizer Localizer = new Localizer("ja-jp",
            () => new List<(string, Func<string, string>)>
            {
                // 未知のキー（NDMF が探す "key:description" / "key:hint" を含む）は null を返す
                ("ja-jp", key => Ja.TryGetValue(key, out var s) ? s : null),
            });

        /// <summary>
        /// 警告を出す。args の先頭から {0}, {1}, … に入る。UnityEngine.Object を渡すとその位置の {n} を消費するため、
        /// クリックで飛べる参照を付けたいときは書式の引数の後ろに置く。
        /// </summary>
        public static void Warn(string key, params object[] args)
        {
            ErrorReport.ReportError(Localizer, ErrorSeverity.NonFatal, key, args);
        }

        /// <summary>NDMF のレポート（ウィンドウ）には出さず、Unity のコンソールにだけ警告を出す。</summary>
        public static void LogWarning(string key, UnityEngine.Object context, params object[] args)
        {
            UnityEngine.Debug.LogWarning("[SDF Auto Generator] " + string.Format(Ja[key], args), context);
        }
    }
}
