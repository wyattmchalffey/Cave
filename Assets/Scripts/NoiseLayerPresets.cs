using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEditor;

public static class NoiseLayerPresets
{
    public static NoiseLayerStack CreateLargeCavernSystem()
    {
        var stack = new NoiseLayerStack();
        stack.layers.Clear();
        
        // Layer 1: Main cavern structure
        var mainCaverns = new NoiseLayer
        {
            layerName = "Main Caverns",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Cavern,
            blendMode = NoiseLayer.BlendMode.Override,
            frequency = 0.015f,
            amplitude = 1.2f,
            octaves = 3,
            persistence = 0.5f,
            lacunarity = 2f,
            verticalSquash = 0.2f, // Very flat caverns
            densityBias = -0.1f,
            power = 1.5f
        };
        stack.layers.Add(mainCaverns);
        
        // Layer 2: Secondary cave network
        var secondaryCaves = new NoiseLayer
        {
            layerName = "Secondary Network",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Worley,
            blendMode = NoiseLayer.BlendMode.Min,
            frequency = 0.03f,
            amplitude = 0.8f,
            octaves = 2,
            persistence = 0.6f,
            lacunarity = 2.5f,
            verticalSquash = 0.3f,
            densityBias = 0.2f,
            power = 2f
        };
        stack.layers.Add(secondaryCaves);
        
        // Layer 3: Connection tunnels
        var tunnels = new NoiseLayer
        {
            layerName = "Connection Tunnels",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Ridged,
            blendMode = NoiseLayer.BlendMode.Subtract,
            frequency = 0.05f,
            amplitude = 0.3f,
            octaves = 2,
            persistence = 0.4f,
            lacunarity = 3f,
            verticalSquash = 0.5f,
            densityBias = 0f,
            power = 1f
        };
        stack.layers.Add(tunnels);
        
        // Layer 4: Detail erosion
        var detail = new NoiseLayer
        {
            layerName = "Erosion Detail",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Perlin,
            blendMode = NoiseLayer.BlendMode.Add,
            frequency = 0.1f,
            amplitude = 0.1f,
            octaves = 4,
            persistence = 0.7f,
            lacunarity = 2f,
            verticalSquash = 1f,
            densityBias = 0f,
            power = 1f
        };
        stack.layers.Add(detail);
        
        return stack;
    }
    
    public static NoiseLayerStack CreateTunnelNetwork()
    {
        var stack = new NoiseLayerStack();
        stack.layers.Clear();
        
        // Layer 1: Primary tunnel network
        var mainTunnels = new NoiseLayer
        {
            layerName = "Main Tunnels",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Ridged,
            blendMode = NoiseLayer.BlendMode.Override,
            frequency = 0.04f,
            amplitude = 1f,
            octaves = 3,
            persistence = 0.6f,
            lacunarity = 2.2f,
            verticalSquash = 0.7f,
            densityBias = 0.3f,
            power = 2f
        };
        stack.layers.Add(mainTunnels);
        
        // Layer 2: Intersecting passages
        var crossTunnels = new NoiseLayer
        {
            layerName = "Cross Tunnels",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Ridged,
            blendMode = NoiseLayer.BlendMode.Min,
            frequency = 0.035f,
            amplitude = 0.8f,
            octaves = 2,
            persistence = 0.5f,
            lacunarity = 2.5f,
            verticalSquash = 0.8f,
            densityBias = 0.4f,
            power = 1.8f,
            offset = new float3(100, 0, 100) // Offset to create different pattern
        };
        stack.layers.Add(crossTunnels);
        
        // Layer 3: Small chambers at intersections
        var chambers = new NoiseLayer
        {
            layerName = "Junction Chambers",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Worley,
            blendMode = NoiseLayer.BlendMode.Subtract,
            frequency = 0.08f,
            amplitude = 0.4f,
            octaves = 1,
            persistence = 0.5f,
            lacunarity = 2f,
            verticalSquash = 0.5f,
            densityBias = 0.5f,
            power = 3f
        };
        stack.layers.Add(chambers);
        
        return stack;
    }
    
    public static NoiseLayerStack CreateVerticalShafts()
    {
        var stack = new NoiseLayerStack();
        stack.layers.Clear();
        
        // Layer 1: Vertical shafts
        var shafts = new NoiseLayer
        {
            layerName = "Vertical Shafts",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Worley,
            blendMode = NoiseLayer.BlendMode.Override,
            frequency = 0.05f,
            amplitude = 1f,
            octaves = 1,
            persistence = 0.5f,
            lacunarity = 2f,
            verticalSquash = 3f, // Stretched vertically
            densityBias = 0.6f,
            power = 4f
        };
        stack.layers.Add(shafts);
        
        // Layer 2: Horizontal galleries
        var galleries = new NoiseLayer
        {
            layerName = "Horizontal Galleries",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Perlin,
            blendMode = NoiseLayer.BlendMode.Subtract,
            frequency = 0.02f,
            amplitude = 0.6f,
            octaves = 2,
            persistence = 0.4f,
            lacunarity = 3f,
            verticalSquash = 0.1f, // Very flat
            densityBias = 0f,
            power = 1f,
            useHeightConstraints = true,
            minHeight = -30f,
            maxHeight = 30f
        };
        
        // Set up height-based galleries
        galleries.heightFalloff = AnimationCurve.Linear(0f, 0f, 1f, 0f);
        var keys = new Keyframe[]
        {
            new Keyframe(0.2f, 1f),
            new Keyframe(0.4f, 0f),
            new Keyframe(0.6f, 1f),
            new Keyframe(0.8f, 0f)
        };
        galleries.heightFalloff = new AnimationCurve(keys);
        
        stack.layers.Add(galleries);
        
        return stack;
    }
    
    public static NoiseLayerStack CreateMassiveChamber()
    {
        var stack = new NoiseLayerStack();
        stack.layers.Clear();
        
        // Layer 1: Massive central chamber
        var chamber = new NoiseLayer
        {
            layerName = "Massive Chamber",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Worley,
            blendMode = NoiseLayer.BlendMode.Override,
            frequency = 0.01f,
            amplitude = 1.5f,
            octaves = 1,
            persistence = 0.5f,
            lacunarity = 2f,
            verticalSquash = 0.15f, // Extremely flat
            densityBias = -0.8f, // Very hollow
            power = 5f // Sharp edges
        };
        stack.layers.Add(chamber);
        
        // Layer 2: Pillar formations
        var pillars = new NoiseLayer
        {
            layerName = "Support Pillars",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Worley,
            blendMode = NoiseLayer.BlendMode.Add,
            frequency = 0.1f,
            amplitude = 0.8f,
            octaves = 1,
            persistence = 0.5f,
            lacunarity = 2f,
            verticalSquash = 5f, // Stretched vertically for pillars
            densityBias = 0.7f,
            power = 3f,
            offset = new float3(50, 0, 50)
        };
        stack.layers.Add(pillars);
        
        // Layer 3: Ceiling detail
        var ceiling = new NoiseLayer
        {
            layerName = "Ceiling Features",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Ridged,
            blendMode = NoiseLayer.BlendMode.Add,
            frequency = 0.2f,
            amplitude = 0.2f,
            octaves = 3,
            persistence = 0.6f,
            lacunarity = 2.2f,
            verticalSquash = 0.5f,
            densityBias = 0f,
            power = 1f,
            useHeightConstraints = true,
            minHeight = 10f,
            maxHeight = 50f
        };
        stack.layers.Add(ceiling);
        
        return stack;
    }
    
    public static NoiseLayerStack CreateLavatubes()
    {
        var stack = new NoiseLayerStack();
        stack.layers.Clear();
        
        // Layer 1: Smooth tube structure
        var tubes = new NoiseLayer
        {
            layerName = "Lava Tubes",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Perlin,
            blendMode = NoiseLayer.BlendMode.Override,
            frequency = 0.025f,
            amplitude = 1f,
            octaves = 2,
            persistence = 0.3f, // Low persistence for smooth tubes
            lacunarity = 2f,
            verticalSquash = 0.8f,
            densityBias = 0.2f,
            power = 3f // High power for smooth, round tubes
        };
        stack.layers.Add(tubes);
        
        // Layer 2: Tube branching
        var branches = new NoiseLayer
        {
            layerName = "Tube Branches",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Simplex,
            blendMode = NoiseLayer.BlendMode.Min,
            frequency = 0.04f,
            amplitude = 0.7f,
            octaves = 2,
            persistence = 0.4f,
            lacunarity = 2.5f,
            verticalSquash = 0.9f,
            densityBias = 0.3f,
            power = 2.5f,
            offset = new float3(200, 50, 200)
        };
        stack.layers.Add(branches);
        
        // Layer 3: Collapsed sections
        var collapsed = new NoiseLayer
        {
            layerName = "Collapsed Areas",
            enabled = true,
            noiseType = NoiseLayer.NoiseType.Worley,
            blendMode = NoiseLayer.BlendMode.Add,
            frequency = 0.06f,
            amplitude = 0.5f,
            octaves = 1,
            persistence = 0.5f,
            lacunarity = 2f,
            verticalSquash = 0.4f,
            densityBias = 0.8f, // Mostly solid
            power = 2f
        };
        stack.layers.Add(collapsed);
        
        return stack;
    }
    
    public static void ApplyPreset(NoiseLayerStack targetStack, NoiseLayerStack preset)
    {
        targetStack.layers.Clear();
        
        foreach (var layer in preset.layers)
        {
            // Create a deep copy of the layer
            var newLayer = new NoiseLayer
            {
                layerName = layer.layerName,
                enabled = layer.enabled,
                noiseType = layer.noiseType,
                blendMode = layer.blendMode,
                frequency = layer.frequency,
                amplitude = layer.amplitude,
                octaves = layer.octaves,
                persistence = layer.persistence,
                lacunarity = layer.lacunarity,
                offset = layer.offset,
                verticalSquash = layer.verticalSquash,
                densityBias = layer.densityBias,
                power = layer.power,
                useHeightConstraints = layer.useHeightConstraints,
                minHeight = layer.minHeight,
                maxHeight = layer.maxHeight,
                heightFalloff = new AnimationCurve(layer.heightFalloff.keys),
                showPreview = layer.showPreview
            };
            
            // Copy gradient
            newLayer.previewGradient = new Gradient();
            newLayer.previewGradient.SetKeys(layer.previewGradient.colorKeys, layer.previewGradient.alphaKeys);
            
            targetStack.layers.Add(newLayer);
        }
    }
}

// Extension to the editor for preset buttons
public static class NoiseLayerEditorExtensions
{
    public static void DrawPresetButtons(WorldManager worldManager)
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);
        
        EditorGUILayout.BeginHorizontal();
        
        if (GUILayout.Button("Large Caverns"))
        {
            var preset = NoiseLayerPresets.CreateLargeCavernSystem();
            NoiseLayerPresets.ApplyPreset(worldManager.noiseLayerStack, preset);
            EditorUtility.SetDirty(worldManager);
        }
        
        if (GUILayout.Button("Tunnel Network"))
        {
            var preset = NoiseLayerPresets.CreateTunnelNetwork();
            NoiseLayerPresets.ApplyPreset(worldManager.noiseLayerStack, preset);
            EditorUtility.SetDirty(worldManager);
        }
        
        if (GUILayout.Button("Vertical Shafts"))
        {
            var preset = NoiseLayerPresets.CreateVerticalShafts();
            NoiseLayerPresets.ApplyPreset(worldManager.noiseLayerStack, preset);
            EditorUtility.SetDirty(worldManager);
        }
        
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        
        if (GUILayout.Button("Massive Chamber"))
        {
            var preset = NoiseLayerPresets.CreateMassiveChamber();
            NoiseLayerPresets.ApplyPreset(worldManager.noiseLayerStack, preset);
            EditorUtility.SetDirty(worldManager);
        }
        
        if (GUILayout.Button("Lava Tubes"))
        {
            var preset = NoiseLayerPresets.CreateLavatubes();
            NoiseLayerPresets.ApplyPreset(worldManager.noiseLayerStack, preset);
            EditorUtility.SetDirty(worldManager);
        }
        
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space();
    }
}