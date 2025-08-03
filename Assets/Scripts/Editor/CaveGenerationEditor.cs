using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

[CustomEditor(typeof(WorldManager))]
public class CaveGenerationEditor : Editor
{
    private WorldManager worldManager;
    private bool showDebugVisualization = false;
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
        serializedObject.Update();
        
        // Custom header
        EditorGUILayout.LabelField("Cave Generation System", EditorStyles.boldLabel);
        EditorGUILayout.Space();
        
        // Generation controls
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Generation Controls", EditorStyles.boldLabel);
        
        if (Application.isPlaying)
        {
            if (GUILayout.Button("Force Update All Chunks", GUILayout.Height(30)))
            {
                worldManager.ForceUpdateAllChunks();
            }
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear All Chunks"))
            {
                ClearAllChunks();
            }
            
            if (GUILayout.Button("Regenerate"))
            {
                ClearAllChunks();
                worldManager.UpdateChunks();
            }
            EditorGUILayout.EndHorizontal();
        }
        else
        {
            EditorGUILayout.HelpBox("Enter Play Mode to use generation controls", MessageType.Info);
        }
        
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space();
        
        // Chunk Management section
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Chunk Management", EditorStyles.boldLabel);
        
        var chunkPrefab = serializedObject.FindProperty("chunkPrefab");
        var renderDistance = serializedObject.FindProperty("renderDistance");
        var chunkSize = serializedObject.FindProperty("chunkSize");
        var voxelSize = serializedObject.FindProperty("voxelSize");
        
        if (chunkPrefab != null) EditorGUILayout.PropertyField(chunkPrefab);
        if (renderDistance != null) EditorGUILayout.PropertyField(renderDistance);
        if (chunkSize != null) EditorGUILayout.PropertyField(chunkSize);
        if (voxelSize != null) EditorGUILayout.PropertyField(voxelSize);
        
        EditorGUILayout.EndVertical();
        
        // Optimization section
        EditorGUILayout.Space();
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Optimization", EditorStyles.boldLabel);
        
        var useJobSystem = serializedObject.FindProperty("useJobSystem");
        var useGPUGeneration = serializedObject.FindProperty("useGPUGeneration");
        var maxConcurrentJobs = serializedObject.FindProperty("maxConcurrentJobs");
        
        if (useJobSystem != null) 
        {
            EditorGUILayout.PropertyField(useJobSystem);
            if (useJobSystem.boolValue && maxConcurrentJobs != null)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(maxConcurrentJobs);
                EditorGUI.indentLevel--;
            }
        }
        
        if (useGPUGeneration != null) EditorGUILayout.PropertyField(useGPUGeneration);
        
        EditorGUILayout.EndVertical();
        
        // Material
        EditorGUILayout.Space();
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Rendering", EditorStyles.boldLabel);
        
        var caveMaterial = serializedObject.FindProperty("caveMaterial");
        if (caveMaterial != null) EditorGUILayout.PropertyField(caveMaterial);
        
        EditorGUILayout.EndVertical();
        
        // Noise Layer Section
        EditorGUILayout.Space();
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
            if (worldManager.noiseLayerStack != null && worldManager.noiseLayerStack.layers != null)
            {
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
            }
            
            EditorGUI.indentLevel--;
        }
        
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space();
        
        // Cave Settings (simplified)
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Height Constraints", EditorStyles.boldLabel);
        
        var settings = worldManager.caveSettings;
        
        settings.minCaveHeight = EditorGUILayout.FloatField("Min Cave Height", settings.minCaveHeight);
        settings.maxCaveHeight = EditorGUILayout.FloatField("Max Cave Height", settings.maxCaveHeight);
        settings.surfaceTransitionHeight = EditorGUILayout.FloatField(
            new GUIContent("Surface Transition", "Height where caves blend with surface"), 
            settings.surfaceTransitionHeight);
        
        worldManager.caveSettings = settings;
        
        EditorGUILayout.EndVertical();
        
        // Random seed
        EditorGUILayout.Space();
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Randomization", EditorStyles.boldLabel);
        
        var seed = worldManager.caveSettings.seed;
        seed = EditorGUILayout.IntField("World Seed", seed);
        if (seed != worldManager.caveSettings.seed)
        {
            var s = worldManager.caveSettings;
            s.seed = seed;
            worldManager.caveSettings = s;
        }
        
        if (GUILayout.Button("Randomize Seed"))
        {
            var s = worldManager.caveSettings;
            s.seed = Random.Range(0, int.MaxValue);
            worldManager.caveSettings = s;
            
            if (Application.isPlaying)
            {
                ClearAllChunks();
                worldManager.UpdateChunks();
            }
        }
        
        EditorGUILayout.EndVertical();
        
        // Debug Visualization
        EditorGUILayout.Space();
        EditorGUILayout.BeginVertical("box");
        showDebugVisualization = EditorGUILayout.Foldout(showDebugVisualization, "Debug Info", true);
        if (showDebugVisualization)
        {
            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField($"Active Chunks: {worldManager.totalChunksLoaded}");
                EditorGUILayout.LabelField($"Total Vertices: {worldManager.totalVertices:N0}");
                
                if (worldManager.chunks != null)
                {
                    EditorGUILayout.LabelField($"Chunk Pool Size: {worldManager.chunks.Count}");
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Debug info available in Play Mode", MessageType.Info);
            }
            
            EditorGUILayout.Space();
            
            worldManager.showDebugInfo = EditorGUILayout.Toggle("Show Debug Gizmos", worldManager.showDebugInfo);
        }
        EditorGUILayout.EndVertical();
        
        serializedObject.ApplyModifiedProperties();
        
        // Apply changes
        if (GUI.changed)
        {
            EditorUtility.SetDirty(worldManager);
            
            // Force chunk updates if in play mode (throttled)
            if (Application.isPlaying && Time.frameCount % 10 == 0)
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
        EditorGUI.BeginChangeCheck();
        
        var so = new SerializedObject(worldManager);
        var layersProp = so.FindProperty("noiseLayerStack").FindPropertyRelative("layers");
        var layerProp = layersProp.GetArrayElementAtIndex(index);
        
        EditorGUILayout.PropertyField(layerProp, GUIContent.none);
        
        if (EditorGUI.EndChangeCheck())
        {
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(worldManager);
        }
        
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
    
    void ClearAllChunks()
    {
        var allChunks = new List<Vector3Int>(worldManager.chunks.Keys);
        foreach (var coord in allChunks)
        {
            worldManager.RemoveChunk(coord);
        }
        worldManager.chunkGenerationQueue.Clear();
    }
}