using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ParallexWithAnimation))]
public class ParallexWithAnimationEditor : Editor
{
    private bool _advancedOpen;

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // ── Movement banner
        Rect banner = EditorGUILayout.GetControlRect(false, 28);
        EditorGUI.DrawRect(banner, new Color(0.13f, 0.22f, 0.35f, 0.85f));
        EditorGUI.LabelField(banner, "  PARALLAX MOVEMENT  —  Inspector only. Runtime code is not changed.", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        // ── Default visible fields
        EditorGUILayout.PropertyField(serializedObject.FindProperty("cameraTransform"),     new GUIContent("Camera"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("translationStrength"), new GUIContent("Parallax Translation Strength"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("rotationStrength"),    new GUIContent("Parallax Rotation Strength"));

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Layers  (0 = Far → last = Near)", EditorStyles.boldLabel);
        SerializedProperty layers = serializedObject.FindProperty("layers");
        for (int i = 0; i < layers.arraySize; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string label = i == 0 ? $"Far (Layer {i})" : i == layers.arraySize - 1 ? $"Near (Layer {i})" : $"Layer {i}";
                EditorGUILayout.PropertyField(layers.GetArrayElementAtIndex(i), new GUIContent(label));
                if (GUILayout.Button("X", GUILayout.Width(24)))
                {
                    // Transform list: first delete nulls element, second removes it
                    if (layers.GetArrayElementAtIndex(i).objectReferenceValue != null)
                        layers.DeleteArrayElementAtIndex(i);
                    layers.DeleteArrayElementAtIndex(i);
                    break;
                }
            }
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add Layer")) layers.InsertArrayElementAtIndex(layers.arraySize);
            if (layers.arraySize > 0 && GUILayout.Button("Remove Last", GUILayout.Width(100)))
            {
                int last = layers.arraySize - 1;
                if (layers.GetArrayElementAtIndex(last).objectReferenceValue != null)
                    layers.DeleteArrayElementAtIndex(last);
                layers.DeleteArrayElementAtIndex(layers.arraySize - 1);
            }
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("gap"), new GUIContent("Depth Gap"));

        // ── Advanced foldout (closed by default)
        EditorGUILayout.Space(6);
        _advancedOpen = EditorGUILayout.Foldout(_advancedOpen, "Advanced Movement Settings", true, EditorStyles.foldoutHeader);
        if (_advancedOpen)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(serializedObject.FindProperty("gapAxis"),                 new GUIContent("Gap Axis"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("hardLockGapAxis"),         new GUIContent("Hard Lock Gap Axis"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("applyGapInEditMode"),      new GUIContent("Apply Gap In Edit Mode"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("invertTranslation"),       new GUIContent("Invert Translation"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("invertRotation"),          new GUIContent("Invert Rotation"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("rotationClampDegrees"),    new GUIContent("Rotation Clamp Degrees"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("rotationToOffsetScale"),   new GUIContent("Rotation To Offset Scale"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("depthWeightCurve"),        new GUIContent("Depth Weight Curve"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxOffset"),               new GUIContent("Max Offset"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("poseFilterTime"),          new GUIContent("Pose Filter Time"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("deadzone"),                new GUIContent("Deadzone"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("layerSmoothTime"),         new GUIContent("Layer Smooth Time"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enforceStableRenderOrder"),new GUIContent("Enforce Stable Render Order"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("nearLayerOnTop"),          new GUIContent("Near Layer On Top"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sortingOrderBase"),        new GUIContent("Sorting Order Base"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sortingOrderStep"),        new GUIContent("Sorting Order Step"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("setSiblingOrderFallback"), new GUIContent("Set Sibling Order Fallback"));
            EditorGUI.indentLevel--;
        }

        serializedObject.ApplyModifiedProperties();
    }
}
