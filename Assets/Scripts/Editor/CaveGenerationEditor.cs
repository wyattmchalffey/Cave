using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(WorldManager))]
public class CaveGenerationEditor : Editor
{
    private WorldManager worldManager;
    private bool showChamberSettings = true;
    private bool showTunnelSettings = true;
    private bool showGeologicalSettings = true;
    private bool showOptimizationSettings = true;
    private bool showDebugVisualization = false;
    
    void OnEnable()
    {
        worldManager = (WorldManager)target;
    }
    
    public override void OnInspectorGUI()
    {
        // Custom header
        EditorGUILayout.LabelField("Cave Generation System", EditorStyles.boldLabel);
        EditorGUILayout.Space();
        
        // Generation controls
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Generation Controls", EditorStyles.boldLabel);
        
        if (GUILayout.Button("Generate Cave Network", GUILayout.Height(30)))
        {
            GenerateCaveNetwork();
        }
        
        if (Application.isPlaying)
        {
            if (GUILayout.Button("Clear All Chunks"))
            {
                ClearAllChunks();
            }
            
            if (GUILayout.Button("Force Update Chunks"))
            {
                ForceUpdateChunks();
            }
        }
        
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space();
        
        // Cave Settings
        EditorGUILayout.BeginVertical("box");
        showChamberSettings = EditorGUILayout.Foldout(showChamberSettings, "Chamber Settings", true);
        if (showChamberSettings)
        {
            var settings = worldManager.caveSettings;
            
            settings.chamberFrequency = EditorGUILayout.Slider(
                "Chamber Frequency", settings.chamberFrequency, 0.01f, 0.1f);
            
            EditorGUILayout.MinMaxSlider("Chamber Radius Range",
                ref settings.chamberMinRadius, ref settings.chamberMaxRadius, 5f, 50f);
            EditorGUILayout.LabelField($"Min: {settings.chamberMinRadius:F1}, Max: {settings.chamberMaxRadius:F1}");
            
            settings.chamberFloorFlatness = EditorGUILayout.Slider(
                "Floor Flatness", settings.chamberFloorFlatness, 0f, 1f);
            
            settings.chamberVerticalScale = EditorGUILayout.Slider(
                "Vertical Scale", settings.chamberVerticalScale, 0.1f, 1f);
            
            worldManager.caveSettings = settings;
        }
        EditorGUILayout.EndVertical();
        
        // Tunnel Settings
        EditorGUILayout.BeginVertical("box");
        showTunnelSettings = EditorGUILayout.Foldout(showTunnelSettings, "Tunnel Settings", true);
        if (showTunnelSettings)
        {
            var settings = worldManager.caveSettings;
            
            EditorGUILayout.MinMaxSlider("Tunnel Radius Range",
                ref settings.tunnelMinRadius, ref settings.tunnelMaxRadius, 1f, 10f);
            EditorGUILayout.LabelField($"Min: {settings.tunnelMinRadius:F1}, Max: {settings.tunnelMaxRadius:F1}");
            
            settings.tunnelCurvature = EditorGUILayout.Slider(
                "Tunnel Curvature", settings.tunnelCurvature, 0f, 1f);
            
            settings.tunnelFrequency = EditorGUILayout.Slider(
                "Tunnel Frequency", settings.tunnelFrequency, 0.01f, 0.1f);
            
            settings.tunnelConnectionsPerChamber = EditorGUILayout.IntSlider(
                "Connections Per Chamber", settings.tunnelConnectionsPerChamber, 1, 6);
            
            worldManager.caveSettings = settings;
        }
        EditorGUILayout.EndVertical();
        
        // Geological Settings
        EditorGUILayout.BeginVertical("box");
        showGeologicalSettings = EditorGUILayout.Foldout(showGeologicalSettings, "Geological Settings", true);
        if (showGeologicalSettings)
        {
            var settings = worldManager.caveSettings;
            
            settings.stratificationStrength = EditorGUILayout.Slider(
                "Stratification Strength", settings.stratificationStrength, 0f, 0.5f);
            
            settings.stratificationFrequency = EditorGUILayout.Slider(
                "Stratification Frequency", settings.stratificationFrequency, 0.05f, 0.5f);
            
            settings.erosionStrength = EditorGUILayout.Slider(
                "Erosion Strength", settings.erosionStrength, 0f, 1f);
            
            settings.rockHardness = EditorGUILayout.Slider(
                "Rock Hardness", settings.rockHardness, 0f, 1f);
            
            EditorGUILayout.Space();
            
            EditorGUILayout.LabelField("Height Constraints");
            settings.minCaveHeight = EditorGUILayout.FloatField("Min Cave Height", settings.minCaveHeight);
            settings.maxCaveHeight = EditorGUILayout.FloatField("Max Cave Height", settings.maxCaveHeight);
            settings.surfaceTransitionHeight = EditorGUILayout.FloatField(
                "Surface Transition Height", settings.surfaceTransitionHeight);
            
            worldManager.caveSettings = settings;
        }
        EditorGUILayout.EndVertical();
        
        // Optimization Settings
        EditorGUILayout.BeginVertical("box");
        showOptimizationSettings = EditorGUILayout.Foldout(showOptimizationSettings, "Optimization Settings", true);
        if (showOptimizationSettings)
        {
            DrawDefaultInspector();
        }
        EditorGUILayout.EndVertical();
        
        // Debug Visualization
        EditorGUILayout.BeginVertical("box");
        showDebugVisualization = EditorGUILayout.Foldout(showDebugVisualization, "Debug Visualization", true);
        if (showDebugVisualization && Application.isPlaying)
        {
            EditorGUILayout.LabelField($"Active Chunks: {worldManager.totalChunksLoaded}");
            EditorGUILayout.LabelField($"Total Vertices: {worldManager.totalVertices:N0}");
            
            if (worldManager.networkPreprocessor != null && worldManager.networkPreprocessor.chamberCenters.IsCreated)
            {
                EditorGUILayout.LabelField($"Chambers: {worldManager.networkPreprocessor.chamberCenters.Length}");
                EditorGUILayout.LabelField($"Tunnels: {worldManager.networkPreprocessor.tunnelNetwork?.Count ?? 0}");
            }
        }
        EditorGUILayout.EndVertical();
        
        // Random seed
        EditorGUILayout.Space();
        var seed = worldManager.caveSettings.seed;
        seed = EditorGUILayout.IntField("Random Seed", seed);
        if (seed != worldManager.caveSettings.seed)
        {
            var s = worldManager.caveSettings;
            s.seed = seed;
            worldManager.caveSettings = s;
        }
        
        if (GUI.changed)
        {
            EditorUtility.SetDirty(worldManager);
        }
    }
    
    void GenerateCaveNetwork()
    {
        if (worldManager.networkPreprocessor == null)
        {
            worldManager.networkPreprocessor = worldManager.gameObject.AddComponent<CaveNetworkPreprocessor>();
        }
        
        worldManager.networkPreprocessor.GenerateCaveNetwork(worldManager.caveSettings);
        
        Debug.Log("Cave network generated successfully!");
    }
    
    void ClearAllChunks()
    {
        // This would clear all active chunks
        Debug.Log("Clearing all chunks...");
    }
    
    void ForceUpdateChunks()
    {
        // This would force chunk updates
        Debug.Log("Forcing chunk update...");
    }
}

// Property drawer for CaveSettings
[CustomPropertyDrawer(typeof(CaveSettings))]
public class CaveSettingsDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);
        
        // Draw label
        position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);
        
        // Don't indent child fields
        var indent = EditorGUI.indentLevel;
        EditorGUI.indentLevel = 0;
        
        // Draw fields - you can add custom layout here
        
        EditorGUI.indentLevel = indent;
        EditorGUI.EndProperty();
    }
}