using UnityEngine;
using UnityEditor;
using Unity.Mathematics;
using System.Collections.Generic;

[CustomPropertyDrawer(typeof(NoiseLayer))]
public class NoiseLayerDrawer : PropertyDrawer
{
    private const int PREVIEW_SIZE = 128;
    private static Dictionary<string, bool> foldoutStates = new Dictionary<string, bool>();
    
    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        string key = property.propertyPath;
        if (!foldoutStates.ContainsKey(key))
            foldoutStates[key] = false;
            
        if (!foldoutStates[key])
            return EditorGUIUtility.singleLineHeight;
            
        // Count visible properties
        float height = EditorGUIUtility.singleLineHeight * 2; // Header + enabled
        var showPreview = property.FindPropertyRelative("showPreview");
        
        // Add height for all properties
        height += EditorGUIUtility.singleLineHeight * 20; // Approximate number of fields
        
        // Add preview height if enabled
        if (showPreview.boolValue)
        {
            height += PREVIEW_SIZE + EditorGUIUtility.singleLineHeight * 2;
        }
        
        return height;
    }
    
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);
        
        string key = property.propertyPath;
        var layerName = property.FindPropertyRelative("layerName");
        var enabled = property.FindPropertyRelative("enabled");
        
        // Header
        Rect headerRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
        Rect foldoutRect = new Rect(headerRect.x, headerRect.y, headerRect.width - 40, headerRect.height);
        Rect enabledRect = new Rect(headerRect.x + headerRect.width - 35, headerRect.y, 35, headerRect.height);
        
        // Draw foldout with custom label
        string headerLabel = string.IsNullOrEmpty(layerName.stringValue) ? "Unnamed Layer" : layerName.stringValue;
        if (!enabled.boolValue) headerLabel += " (Disabled)";
        
        foldoutStates[key] = EditorGUI.Foldout(foldoutRect, foldoutStates[key], headerLabel, true);
        enabled.boolValue = EditorGUI.Toggle(enabledRect, enabled.boolValue);
        
        if (foldoutStates[key])
        {
            float y = position.y + EditorGUIUtility.singleLineHeight;
            float lineHeight = EditorGUIUtility.singleLineHeight;
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel++;
            
            // Layer Name
            EditorGUI.PropertyField(new Rect(position.x, y, position.width, lineHeight), layerName);
            y += lineHeight;
            
            // Noise Type and Blend Mode
            var noiseType = property.FindPropertyRelative("noiseType");
            var blendMode = property.FindPropertyRelative("blendMode");
            
            float halfWidth = position.width * 0.5f - 5;
            EditorGUI.PropertyField(new Rect(position.x, y, halfWidth, lineHeight), noiseType);
            EditorGUI.PropertyField(new Rect(position.x + halfWidth + 10, y, halfWidth, lineHeight), blendMode);
            y += lineHeight + 5;
            
            // Noise Parameters Section
            EditorGUI.LabelField(new Rect(position.x, y, position.width, lineHeight), "Noise Parameters", EditorStyles.boldLabel);
            y += lineHeight;
            
            DrawProperty(property, "frequency", ref y, position);
            DrawProperty(property, "amplitude", ref y, position);
            DrawProperty(property, "octaves", ref y, position);
            DrawProperty(property, "persistence", ref y, position);
            DrawProperty(property, "lacunarity", ref y, position);
            DrawProperty(property, "offset", ref y, position);
            
            // Shape Modifiers Section
            y += 5;
            EditorGUI.LabelField(new Rect(position.x, y, position.width, lineHeight), "Shape Modifiers", EditorStyles.boldLabel);
            y += lineHeight;
            
            DrawProperty(property, "verticalSquash", ref y, position);
            DrawProperty(property, "densityBias", ref y, position);
            DrawProperty(property, "power", ref y, position);
            
            // Constraints Section
            y += 5;
            EditorGUI.LabelField(new Rect(position.x, y, position.width, lineHeight), "Height Constraints", EditorStyles.boldLabel);
            y += lineHeight;
            
            DrawProperty(property, "useHeightConstraints", ref y, position);
            if (property.FindPropertyRelative("useHeightConstraints").boolValue)
            {
                EditorGUI.indentLevel++;
                DrawProperty(property, "minHeight", ref y, position);
                DrawProperty(property, "maxHeight", ref y, position);
                DrawProperty(property, "heightFalloff", ref y, position);
                EditorGUI.indentLevel--;
            }
            
            // Preview Section
            y += 5;
            EditorGUI.LabelField(new Rect(position.x, y, position.width, lineHeight), "Visualization", EditorStyles.boldLabel);
            y += lineHeight;
            
            var showPreview = property.FindPropertyRelative("showPreview");
            EditorGUI.PropertyField(new Rect(position.x, y, position.width, lineHeight), showPreview);
            y += lineHeight;
            
            if (showPreview.boolValue)
            {
                DrawProperty(property, "previewGradient", ref y, position);
                
                // Generate and display preview texture
                Rect previewRect = new Rect(position.x + EditorGUI.indentLevel * 15, y, PREVIEW_SIZE, PREVIEW_SIZE);
                DrawNoisePreview(property, previewRect);
                
                // Add refresh button
                if (GUI.Button(new Rect(previewRect.x + PREVIEW_SIZE + 10, previewRect.y, 60, 20), "Refresh"))
                {
                    property.serializedObject.ApplyModifiedProperties();
                }
            }
            
            EditorGUI.indentLevel = indent;
        }
        
        EditorGUI.EndProperty();
    }
    
    private void DrawProperty(SerializedProperty parent, string propertyName, ref float y, Rect position)
    {
        var prop = parent.FindPropertyRelative(propertyName);
        if (prop != null)
        {
            float height = EditorGUI.GetPropertyHeight(prop);
            EditorGUI.PropertyField(new Rect(position.x, y, position.width, height), prop);
            y += height;
        }
    }
    
    private void DrawNoisePreview(SerializedProperty property, Rect rect)
    {
        // Get or create preview texture
        var previewTexture = GetPreviewTexture(property);
        if (previewTexture != null)
        {
            EditorGUI.DrawPreviewTexture(rect, previewTexture);
            
            // Draw border
            GUI.Box(rect, GUIContent.none);
        }
    }
    
    private Texture2D GetPreviewTexture(SerializedProperty property)
    {
        // Create a temporary NoiseLayer to evaluate
        var layer = new NoiseLayer
        {
            enabled = true,
            noiseType = (NoiseLayer.NoiseType)property.FindPropertyRelative("noiseType").enumValueIndex,
            frequency = property.FindPropertyRelative("frequency").floatValue,
            amplitude = property.FindPropertyRelative("amplitude").floatValue,
            octaves = property.FindPropertyRelative("octaves").intValue,
            persistence = property.FindPropertyRelative("persistence").floatValue,
            lacunarity = property.FindPropertyRelative("lacunarity").floatValue,
            verticalSquash = property.FindPropertyRelative("verticalSquash").floatValue,
            densityBias = property.FindPropertyRelative("densityBias").floatValue,
            power = property.FindPropertyRelative("power").floatValue
        };
        
        // Create texture
        var texture = new Texture2D(PREVIEW_SIZE, PREVIEW_SIZE, TextureFormat.RGB24, false);
        texture.filterMode = FilterMode.Bilinear;
        
        // Use a simple black to white gradient for preview
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new GradientColorKey[] { 
                new GradientColorKey(Color.black, 0f), 
                new GradientColorKey(Color.white, 1f) 
            },
            new GradientAlphaKey[] { 
                new GradientAlphaKey(1f, 0f), 
                new GradientAlphaKey(1f, 1f) 
            }
        );
        
        // Sample noise and fill texture
        float scale = 50f; // World units to sample
        Color[] pixels = new Color[PREVIEW_SIZE * PREVIEW_SIZE];
        
        for (int y = 0; y < PREVIEW_SIZE; y++)
        {
            for (int x = 0; x < PREVIEW_SIZE; x++)
            {
                float3 samplePos = new float3(
                    (x / (float)PREVIEW_SIZE - 0.5f) * scale,
                    0,
                    (y / (float)PREVIEW_SIZE - 0.5f) * scale
                );
                
                float value = layer.Evaluate(samplePos);
                value = math.saturate(value * 0.5f + 0.5f); // Remap to 0-1
                
                pixels[y * PREVIEW_SIZE + x] = gradient.Evaluate(value);
            }
        }
        
        texture.SetPixels(pixels);
        texture.Apply();
        
        return texture;
    }
}

[CustomEditor(typeof(WorldManager))]
public class WorldManagerNoiseEditor : Editor
{
    private WorldManager worldManager;
    private bool showNoiseSection = true;
    private Vector2 layerScrollPos;
    
    void OnEnable()
    {
        worldManager = (WorldManager)target;
        
        // Initialize noise stack if needed
        if (worldManager.noiseLayerStack == null)
        {
            worldManager.noiseLayerStack = new NoiseLayerStack();
            EditorUtility.SetDirty(worldManager);
        }
    }
    
    public override void OnInspectorGUI()
    {
        // Draw default inspector first
        DrawDefaultInspector();
        
        EditorGUILayout.Space();
        
        // Noise Layer Section
        EditorGUILayout.BeginVertical("box");
        showNoiseSection = EditorGUILayout.Foldout(showNoiseSection, "Noise Layer System", true);
        
        if (showNoiseSection)
        {
            EditorGUI.indentLevel++;
            
            // Preset buttons
            NoiseLayerEditorExtensions.DrawPresetButtons(worldManager);
            
            // Layer controls
            EditorGUILayout.BeginHorizontal();
            
            if (GUILayout.Button("Add Layer", GUILayout.Width(100)))
            {
                AddNewLayer();
            }
            
            if (GUILayout.Button("Clear All", GUILayout.Width(100)))
            {
                if (EditorUtility.DisplayDialog("Clear All Layers", 
                    "Are you sure you want to remove all noise layers?", "Yes", "No"))
                {
                    worldManager.noiseLayerStack.layers.Clear();
                    EditorUtility.SetDirty(worldManager);
                }
            }
            
            if (GUILayout.Button("Reset to Default", GUILayout.Width(120)))
            {
                worldManager.noiseLayerStack = new NoiseLayerStack();
                EditorUtility.SetDirty(worldManager);
            }
            
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.Space();
            
            // Layer list
            if (worldManager.noiseLayerStack.layers.Count > 0)
            {
                EditorGUILayout.LabelField($"Active Layers: {worldManager.noiseLayerStack.layers.Count}");
                
                layerScrollPos = EditorGUILayout.BeginScrollView(layerScrollPos, GUILayout.MaxHeight(400));
                
                for (int i = 0; i < worldManager.noiseLayerStack.layers.Count; i++)
                {
                    DrawLayerControls(i);
                }
                
                EditorGUILayout.EndScrollView();
            }
            else
            {
                EditorGUILayout.HelpBox("No noise layers. Click 'Add Layer' to create one.", MessageType.Info);
            }
            
            EditorGUI.indentLevel--;
        }
        
        EditorGUILayout.EndVertical();
        
        // Apply changes
        if (GUI.changed)
        {
            EditorUtility.SetDirty(worldManager);
            
            // Force chunk updates if in play mode
            if (Application.isPlaying)
            {
                worldManager.ForceUpdateAllChunks();
            }
        }
    }
    
    private void DrawLayerControls(int index)
    {
        var layer = worldManager.noiseLayerStack.layers[index];
        
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.BeginHorizontal();
        
        // Layer name and enabled state
        layer.enabled = EditorGUILayout.Toggle(layer.enabled, GUILayout.Width(20));
        layer.layerName = EditorGUILayout.TextField(layer.layerName);
        
        // Move buttons
        GUI.enabled = index > 0;
        if (GUILayout.Button("↑", GUILayout.Width(25)))
        {
            worldManager.noiseLayerStack.MoveLayer(index, index - 1);
        }
        GUI.enabled = index < worldManager.noiseLayerStack.layers.Count - 1;
        if (GUILayout.Button("↓", GUILayout.Width(25)))
        {
            worldManager.noiseLayerStack.MoveLayer(index, index + 1);
        }
        GUI.enabled = true;
        
        // Duplicate button
        if (GUILayout.Button("⧉", GUILayout.Width(25)))
        {
            DuplicateLayer(index);
        }
        
        // Delete button
        if (GUILayout.Button("×", GUILayout.Width(25)))
        {
            if (EditorUtility.DisplayDialog("Delete Layer", 
                $"Delete layer '{layer.layerName}'?", "Delete", "Cancel"))
            {
                worldManager.noiseLayerStack.RemoveLayer(index);
                EditorUtility.SetDirty(worldManager);
            }
        }
        
        EditorGUILayout.EndHorizontal();
        
        // Draw layer properties (the property drawer will handle this)
        var so = new SerializedObject(worldManager);
        var layersProp = so.FindProperty("noiseLayerStack").FindPropertyRelative("layers");
        var layerProp = layersProp.GetArrayElementAtIndex(index);
        
        EditorGUILayout.PropertyField(layerProp, GUIContent.none);
        so.ApplyModifiedProperties();
        
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space();
    }
    
    private void AddNewLayer()
    {
        var newLayer = new NoiseLayer();
        
        // Set unique name
        int count = 1;
        string baseName = "New Layer";
        string newName = baseName;
        
        while (LayerNameExists(newName))
        {
            newName = $"{baseName} {count++}";
        }
        
        newLayer.layerName = newName;
        
        // Set some default values based on existing layers
        if (worldManager.noiseLayerStack.layers.Count > 0)
        {
            var lastLayer = worldManager.noiseLayerStack.layers[worldManager.noiseLayerStack.layers.Count - 1];
            newLayer.frequency = lastLayer.frequency * 2f; // Higher frequency for detail
            newLayer.amplitude = lastLayer.amplitude * 0.5f; // Lower amplitude for detail
        }
        
        worldManager.noiseLayerStack.AddLayer(newLayer);
        EditorUtility.SetDirty(worldManager);
    }
    
    private void DuplicateLayer(int index)
    {
        var original = worldManager.noiseLayerStack.layers[index];
        var duplicate = new NoiseLayer
        {
            layerName = original.layerName + " (Copy)",
            enabled = original.enabled,
            noiseType = original.noiseType,
            blendMode = original.blendMode,
            frequency = original.frequency,
            amplitude = original.amplitude,
            octaves = original.octaves,
            persistence = original.persistence,
            lacunarity = original.lacunarity,
            offset = original.offset,
            verticalSquash = original.verticalSquash,
            densityBias = original.densityBias,
            power = original.power,
            useHeightConstraints = original.useHeightConstraints,
            minHeight = original.minHeight,
            maxHeight = original.maxHeight,
            heightFalloff = new AnimationCurve(original.heightFalloff.keys),
            showPreview = original.showPreview,
            previewGradient = new Gradient()
        };
        
        // Copy gradient
        duplicate.previewGradient.SetKeys(original.previewGradient.colorKeys, original.previewGradient.alphaKeys);
        
        worldManager.noiseLayerStack.layers.Insert(index + 1, duplicate);
        EditorUtility.SetDirty(worldManager);
    }
    
    private bool LayerNameExists(string name)
    {
        foreach (var layer in worldManager.noiseLayerStack.layers)
        {
            if (layer.layerName == name) return true;
        }
        return false;
    }
}