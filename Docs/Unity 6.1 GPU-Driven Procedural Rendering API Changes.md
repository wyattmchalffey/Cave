# Unity 6.1 GPU-Driven Procedural Rendering API Changes

Unity 6.1 introduces **critical breaking changes** to GPU-driven procedural rendering APIs, marking the obsolescence of Graphics.DrawProceduralIndirect and Graphics.DrawProcedural in favor of new RenderPrimitives APIs. These changes require immediate attention for existing projects using GPU instancing and indirect rendering workflows, though many supporting APIs maintain backward compatibility.

## Graphics.DrawProceduralIndirect is now obsolete

The most significant change in Unity 6.1 is the **complete deprecation** of Graphics.DrawProceduralIndirect, which now generates compiler warnings and requires migration to new APIs. The function signatures that developers have relied on since Unity 2021 are officially marked obsolete, with Unity's documentation explicitly stating "This function is now obsolete."

### Migration to RenderPrimitivesIndirect

The replacement APIs introduce a fundamentally different parameter structure using the new RenderParams struct. For non-indexed rendering, the new API signature is:

```csharp
public static void RenderPrimitivesIndirect(
    ref RenderParams rparams, 
    MeshTopology topology, 
    GraphicsBuffer commandBuffer, 
    int commandCount = 1, 
    int startCommand = 0
);
```

For indexed rendering with an index buffer:

```csharp
public static void RenderPrimitivesIndexedIndirect(
    ref RenderParams rparams, 
    MeshTopology topology, 
    GraphicsBuffer indexBuffer, 
    GraphicsBuffer commandBuffer, 
    int commandCount = 1, 
    int startCommand = 0
);
```

The migration requires restructuring your code to use the RenderParams structure, which consolidates all rendering parameters:

```csharp
// Before (Unity 2021/2022)
Graphics.DrawProceduralIndirect(
    material, bounds, MeshTopology.Triangles, 
    argsBuffer, 0, camera, materialPropertyBlock, 
    ShadowCastingMode.On, true, 0
);

// After (Unity 6.1)
RenderParams rparams = new RenderParams(material);
rparams.worldBounds = bounds;
rparams.camera = camera;
rparams.matProps = materialPropertyBlock;
rparams.shadowCastingMode = ShadowCastingMode.On;
rparams.receiveShadows = true;
rparams.layer = 0;

Graphics.RenderPrimitivesIndirect(rparams, MeshTopology.Triangles, argsBuffer);
```

## Graphics.DrawProcedural faces identical obsolescence

Graphics.DrawProcedural follows the same deprecation path, with Unity 6.1 marking it as obsolete and directing developers to use Graphics.RenderPrimitives for non-indexed rendering or Graphics.RenderPrimitivesIndexed for indexed rendering. The new APIs maintain similar functionality but require the same RenderParams structure approach:

```csharp
// Non-indexed replacement
public static void RenderPrimitives(
    ref RenderParams rparams, 
    MeshTopology topology, 
    int vertexCount, 
    int instanceCount = 1
);

// Indexed replacement
public static void RenderPrimitivesIndexed(
    ref RenderParams rparams, 
    MeshTopology topology, 
    GraphicsBuffer indexBuffer, 
    int indexCount, 
    int startIndex = 0, 
    int instanceCount = 1
);
```

## ComputeBuffer API maintains stability

Despite the dramatic changes to procedural drawing APIs, **ComputeBuffer remains unchanged** in Unity 6.1, maintaining full backward compatibility with existing code. All constructor patterns, SetData/GetData methods, and ComputeBufferType options continue to function identically to previous versions. Platform-specific limitations persist, with Metal limited to 31 buffers bound simultaneously and Vulkan limits remaining device-dependent.

Unity 6.1 does introduce enhanced support for GraphicsBuffer as a more versatile alternative, particularly for indirect arguments. The transition from ComputeBuffer to GraphicsBuffer for indirect drawing arguments represents a best practice rather than a requirement:

```csharp
// Traditional ComputeBuffer approach (still works)
ComputeBuffer argsBuffer = new ComputeBuffer(
    1, 4 * sizeof(uint), 
    ComputeBufferType.IndirectArguments
);

// Recommended GraphicsBuffer approach
GraphicsBuffer argsBuffer = new GraphicsBuffer(
    GraphicsBuffer.Target.IndirectArguments, 
    1, 
    GraphicsBuffer.IndirectDrawArgs.size
);
```

## StructuredBuffer shader usage unchanged

StructuredBuffer declarations and usage in HLSL shaders remain consistent with previous Unity versions. The binding semantics, declaration patterns, and cross-platform behavior continue unchanged:

```hlsl
// These patterns remain valid in Unity 6.1
StructuredBuffer<float3> _Positions;
RWStructuredBuffer<float3> _Positions;
```

However, shaders using the new RenderPrimitives APIs require inclusion of Unity's indirect drawing framework:

```hlsl
#define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs
#include "UnityIndirect.cginc"

v2f vert(uint svVertexID: SV_VertexID, uint svInstanceID : SV_InstanceID) {
    InitIndirectDrawArgs(0);
    uint cmdID = GetCommandID(0);
    uint instanceID = GetIndirectInstanceID(svInstanceID);
    uint vertexID = GetIndirectVertexID(svVertexID);
    // Vertex transformation logic
}
```

## CommandBuffer API evolves without breaking changes

The CommandBuffer API maintains **complete backward compatibility** while adding new functionality for ray tracing and GPU-driven rendering. Existing methods like DrawProceduralIndirect and DrawMeshInstanced continue to function, though they may face deprecation in future releases. New additions focus on ray tracing acceleration structures:

- RayTracingAccelerationStructure.UpdateInstanceGeometry() for manual BLAS updates
- RayTracingAccelerationStructure.AddInstancesIndirect() for GraphicsBuffer-based matrices
- RayTracingAccelerationStructure.CullInstances() for filtered acceleration structure population

The CommandBuffer variants of procedural drawing methods currently avoid deprecation warnings, providing a temporary migration path for complex rendering systems.

## GPU instancing deprecations and evolution

While core GPU instancing functionality remains intact, Unity 6.1 signals a clear architectural shift toward the new rendering APIs. The **PROCEDURAL_INSTANCING_ON** keyword continues to function, but new projects should adopt the indirect drawing macros required by RenderPrimitives APIs. The transition emphasizes GraphicsBuffer over ComputeBuffer for indirect arguments, though both remain supported.

The relationship between SRP Batcher and GPU instancing remains unchanged but critical to understand: **SRP Batcher takes priority over GPU instancing**, and using MaterialPropertyBlock still breaks SRP Batcher compatibility. This intentional behavior requires careful consideration when optimizing rendering performance.

## RenderPipeline requirements embrace RenderGraph

Unity 6.1 establishes the RenderGraph system as the standard for custom render pipelines, though backward compatibility through "compatibility mode" ensures existing pipelines continue functioning. New projects default to RenderGraph, while upgraded projects automatically enable compatibility mode.

The migration to RenderGraph requires reimplementing custom render passes:

```csharp
// Legacy approach (deprecated but supported in compatibility mode)
public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
    // Traditional implementation
}

// RenderGraph approach (new standard)
public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
    using (var builder = renderGraph.AddRasterRenderPass<PassData>("PassName", out var passData)) {
        builder.SetRenderFunc<PassData>((PassData data, RasterGraphContext context) => {
            // Rendering commands
        });
    }
}
```

Additional pipeline changes include the **Deferred+ rendering path** in URP, offering unlimited light support through cluster-based culling, and **Variable Rate Shading (VRS)** support for DirectX 12, Vulkan, and compatible consoles.

## MaterialPropertyBlock usage remains consistent

MaterialPropertyBlock APIs experience **no breaking changes** in Unity 6.1, with all existing methods maintaining their signatures and behavior. The complex relationship with SRP Batcher persists: using MaterialPropertyBlock removes renderers from SRP Batcher optimization, which remains intentional behavior rather than a bug.

Best practices for MaterialPropertyBlock usage continue unchanged:
- Create once and reuse for efficiency
- Use Shader.PropertyToID() for repeated property access
- Ensure properties are declared as instanced in shaders when used with GPU instancing
- Consider SRP Batcher vs GPU instancing trade-offs based on scene composition

## Shader compilation adopts minimal changes

Shader compilation in Unity 6.1 introduces one **critical breaking change**: the replacement of the `_FORWARD_PLUS` keyword with `_CLUSTER_LIGHT_LOOP`. Custom shaders using Forward+ rendering must update their conditional compilation:

```hlsl
// Old (Unity 6.0 and earlier)
#ifdef _FORWARD_PLUS
    // Forward+ rendering code
#endif

// New (Unity 6.1+)
#ifdef _CLUSTER_LIGHT_LOOP
    // Cluster-based light loop code
#endif
```

All pragma directives for procedural instancing remain unchanged:
```hlsl
#pragma multi_compile_instancing
#pragma instancing_options procedural:setup
#pragma instancing_options assumeuniformscale
```

Performance improvements include a caching shader preprocessor for faster variant compilation and experimental GraphicsStateCollection API for shader pre-warming. DirectX 12 becomes the default graphics API for new Windows projects, potentially improving compute shader performance on compatible hardware.

## Migration strategy and timeline

The deprecation of core procedural rendering APIs demands immediate attention for production projects. While Unity 6.1 maintains these APIs in an obsolete-but-functional state, future releases will likely remove them entirely. Development teams should prioritize migration based on project complexity:

**Immediate actions required:**
1. Replace all Graphics.DrawProceduralIndirect calls with Graphics.RenderPrimitivesIndirect
2. Replace all Graphics.DrawProcedural calls with Graphics.RenderPrimitives
3. Update shaders to include Unity's indirect drawing framework
4. Replace _FORWARD_PLUS with _CLUSTER_LIGHT_LOOP in custom shaders

**Medium-term considerations:**
- Transition from ComputeBuffer to GraphicsBuffer for indirect arguments
- Migrate custom render pipelines to RenderGraph system
- Evaluate CommandBuffer procedural methods for future deprecation

**Performance implications:**
The new APIs provide better integration with modern GPU architectures and Unity's render graph system, with developers reporting performance improvements in complex scenes after migration. The architectural changes position Unity for future GPU-driven rendering enhancements while maintaining a clear, if disruptive, upgrade path.

## Conclusion

Unity 6.1's GPU-driven procedural rendering changes represent the most significant API deprecation in recent Unity history, requiring substantial code changes for projects using these features. While the migration demands careful planning and testing, the new RenderPrimitives APIs offer improved performance and better integration with modern rendering pipelines. The maintenance of backward compatibility in supporting systems like ComputeBuffer and MaterialPropertyBlock eases the transition, but the obsolescence of core drawing APIs makes migration inevitable for projects planning to use future Unity versions.