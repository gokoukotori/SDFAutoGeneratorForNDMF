using nadena.dev.ndmf.runtime;
using UnityEditor;
using UnityEngine;

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    [CustomEditor(typeof(SdfAutoGenerator))]
    internal class SdfAutoGeneratorEditor : UnityEditor.Editor
    {
        static readonly GUIContent[] TargetModeLabels =
        {
            new GUIContent("指定したマテリアル"), new GUIContent("すべてのマテリアル"),
        };

        internal static bool IsMisplaced(SdfAutoGenerator c)
        {
            return !RuntimeUtil.IsAvatarRoot(c.transform);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var c = (SdfAutoGenerator)target;

            if (IsMisplaced(c))
            {
                EditorGUILayout.HelpBox("アバタールートに置いてください。ここに置いたものは無視されます。", MessageType.Warning);
            }

            var mode = serializedObject.FindProperty(nameof(SdfAutoGenerator.targetMode));
            // BeginProperty でプレハブのオーバーライド表示・右クリックの Revert を有効にし、変更したときだけ書き込む
            var rect = EditorGUILayout.GetControlRect();
            var label = EditorGUI.BeginProperty(rect, new GUIContent("対象"), mode);
            EditorGUI.BeginChangeCheck();
            var selected = EditorGUI.Popup(rect, label, mode.enumValueIndex, TargetModeLabels);
            if (EditorGUI.EndChangeCheck()) mode.enumValueIndex = selected;
            EditorGUI.EndProperty();
            if (mode.enumValueIndex == (int)SdfTargetMode.Specified)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(SdfAutoGenerator.materials)),
                    new GUIContent("対象マテリアル"), true);
            }

            // 生成の設定は、入力を確定したとき（Enter・フォーカスを外したとき）だけ書き込む。
            // スライダーやドラッグで 1 段ごとに書き込むと、そのたびにプレビューが全対象を作り直すため。範囲はコンポーネントの Range・Min と同じ
            DelayedFloat(serializedObject.FindProperty(nameof(SdfAutoGenerator.lightElevation)), new GUIContent("光の高さ（°）"),
                0f, SdfAutoGenerator.MaxLightElevation);
            DelayedInt(serializedObject.FindProperty(nameof(SdfAutoGenerator.normalSmoothing)), new GUIContent("法線のなめらかさ"),
                0, SdfAutoGenerator.MaxNormalSmoothing);
            DelayedFloat(serializedObject.FindProperty(nameof(SdfAutoGenerator.shadowDistance)), new GUIContent("落ち影の届く距離（m）"),
                0f, float.MaxValue);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(SdfAutoGenerator.resolution)), new GUIContent("テクスチャ解像度"));

            serializedObject.ApplyModifiedProperties();
        }

        // BeginProperty でプレハブのオーバーライド表示・右クリックの Revert・複数選択の表示を有効にし、確定したときだけ範囲に丸めて書き込む
        static void DelayedFloat(SerializedProperty prop, GUIContent content, float min, float max)
        {
            var rect = EditorGUILayout.GetControlRect();
            var label = EditorGUI.BeginProperty(rect, content, prop);
            EditorGUI.BeginChangeCheck();
            var value = EditorGUI.DelayedFloatField(rect, label, prop.floatValue);
            if (EditorGUI.EndChangeCheck()) prop.floatValue = float.IsNaN(value) ? min : Mathf.Clamp(value, min, max);
            EditorGUI.EndProperty();
        }

        static void DelayedInt(SerializedProperty prop, GUIContent content, int min, int max)
        {
            var rect = EditorGUILayout.GetControlRect();
            var label = EditorGUI.BeginProperty(rect, content, prop);
            EditorGUI.BeginChangeCheck();
            var value = EditorGUI.DelayedIntField(rect, label, prop.intValue);
            if (EditorGUI.EndChangeCheck()) prop.intValue = Mathf.Clamp(value, min, max);
            EditorGUI.EndProperty();
        }
    }
}
