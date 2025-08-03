using UnityEngine;
using UnityEditor;
using Unity.Mathematics;
using System.Collections.Generic;

[CustomPropertyDrawer(typeof(NoiseLayer))]
public class NoiseLayerDrawer : PropertyDrawer
{
    private const int PREVIEW_SIZE = 128;
    private static Dictionary<string, bool> foldoutStates = new Dictionary<string, bool>();
    private static Dictionary<string, Texture2D> previewCache = new Dictionary<string, Texture2D>();
    private static Dictionary<string, int> lastUpdateFrame = new Dictionary<string, int>();
    
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
        string cacheKey = property.propertyPath;
        
        // Check if we need to update the preview
        bool needsUpdate = false;
        
        // Check if texture exists in cache
        if (!previewCache.ContainsKey(cacheKey) || previewCache[cacheKey] == null)
        {
            needsUpdate = true;
        }
        else
        {
            // Only update if property has changed (check every 30 frames)
            int currentFrame = Time.frameCount;
            if (!lastUpdateFrame.ContainsKey(cacheKey))
            {
                lastUpdateFrame[cacheKey] = currentFrame;
                needsUpdate = true;
            }
            else if (currentFrame - lastUpdateFrame[cacheKey] > 30)
            {
                lastUpdateFrame[cacheKey] = currentFrame;
                if (GUI.changed || property.serializedObject.hasModifiedProperties)
                {
                    needsUpdate = true;
                }
            }
        }
        
        // Generate texture if needed
        if (needsUpdate)
        {
            var texture = GetPreviewTexture(property);
            if (texture != null)
            {
                previewCache[cacheKey] = texture;
            }
        }
        
        // Draw cached texture
        if (previewCache.ContainsKey(cacheKey) && previewCache[cacheKey] != null)
        {
            EditorGUI.DrawPreviewTexture(rect, previewCache[cacheKey]);
            
            // Draw border
            GUI.Box(rect, GUIContent.none);
        }
        
        // Add manual refresh button
        if (GUI.Button(new Rect(rect.x + PREVIEW_SIZE + 10, rect.y, 60, 20), "Refresh"))
        {
            if (previewCache.ContainsKey(cacheKey) && previewCache[cacheKey] != null)
            {
                UnityEngine.Object.DestroyImmediate(previewCache[cacheKey]);
            }
            var texture = GetPreviewTexture(property);
            if (texture != null)
            {
                previewCache[cacheKey] = texture;
            }
            property.serializedObject.ApplyModifiedProperties();
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
    
    // Static cleanup method
    static NoiseLayerDrawer()
    {
        // Clean up textures when scripts reload or play mode changes
        EditorApplication.playModeStateChanged += (state) =>
        {
            if (state == PlayModeStateChange.ExitingEditMode || 
                state == PlayModeStateChange.ExitingPlayMode)
            {
                CleanupPreviewCache();
            }
        };
    }
    
    private static void CleanupPreviewCache()
    {
        foreach (var kvp in previewCache)
        {
            if (kvp.Value != null)
            {
                UnityEngine.Object.DestroyImmediate(kvp.Value);
            }
        }
        previewCache.Clear();
        lastUpdateFrame.Clear();
    }
}