// TerrainRenderer.shader - GPU-driven terrain rendering for URP
Shader "GPUTerrain/TerrainRenderer"
{
    Properties
    {
        _BaseMap ("Base Texture", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Metallic ("Metallic", Range(0,1)) = 0.0
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
    }
    
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100
        
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            
            // Properties
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Metallic;
                half _Smoothness;
            CBUFFER_END
            
            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            
            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                StructuredBuffer<float4> _VertexPool; // xyz = position, w = materialID
                StructuredBuffer<float4> _NormalPool; // xyz = normal, w = unused
                
                struct ChunkMetadata
                {
                    float3 position;
                    uint vertexOffset;
                    uint vertexCount;
                    uint indexOffset;
                    uint lodLevel;
                    uint flags;
                };
                
                StructuredBuffer<ChunkMetadata> _ChunkMetadata;
                uint _InstanceID;
            #endif
            
            void setup()
            {
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    _InstanceID = UNITY_GET_INSTANCE_ID(v);
                #endif
            }
            
            Varyings vert(Attributes input, uint vertexID : SV_VertexID)
            {
                Varyings output = (Varyings)0;
                
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                
                float3 positionWS;
                float3 normalWS;
                
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    ChunkMetadata chunk = _ChunkMetadata[_InstanceID];
                    uint poolIndex = chunk.vertexOffset + vertexID;
                    
                    float4 vertexData = _VertexPool[poolIndex];
                    float4 normalData = _NormalPool[poolIndex];
                    
                    positionWS = vertexData.xyz;
                    normalWS = normalData.xyz;
                #else
                    positionWS = TransformObjectToWorld(input.positionOS.xyz);
                    normalWS = TransformObjectToWorldNormal(input.normalOS);
                #endif
                
                output.positionWS = positionWS;
                output.normalWS = normalize(normalWS);
                output.positionHCS = TransformWorldToHClip(positionWS);
                
                // Triplanar UV mapping
                float3 blendWeights = abs(normalWS);
                blendWeights = blendWeights / (blendWeights.x + blendWeights.y + blendWeights.z);
                
                float2 uvX = positionWS.zy * 0.1;
                float2 uvY = positionWS.xz * 0.1;
                float2 uvZ = positionWS.xy * 0.1;
                
                output.uv = uvX * blendWeights.x + uvY * blendWeights.y + uvZ * blendWeights.z;
                output.fogFactor = ComputeFogFactor(output.positionHCS.z);
                
                return output;
            }
            
            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                
                // Sample texture
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                
                // Prepare surface data
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalize(input.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = 0;
                
                // Simple lighting
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo.rgb;
                surfaceData.metallic = _Metallic;
                surfaceData.specular = half3(0.0h, 0.0h, 0.0h);
                surfaceData.smoothness = _Smoothness;
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.emission = 0;
                surfaceData.occlusion = 1;
                surfaceData.alpha = 1;
                surfaceData.clearCoatMask = 0;
                surfaceData.clearCoatSmoothness = 0;
                
                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, input.fogFactor);
                
                return color;
            }
            ENDHLSL
        }
        
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            
            ZWrite On
            ZTest LEqual
            ColorMask 0
            
            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ShadowCasterPass.hlsl"
            
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            
            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                StructuredBuffer<float4> _VertexPool;
                StructuredBuffer<float4> _NormalPool;
                
                struct ChunkMetadata
                {
                    float3 position;
                    uint vertexOffset;
                    uint vertexCount;
                    uint indexOffset;
                    uint lodLevel;
                    uint flags;
                };
                
                StructuredBuffer<ChunkMetadata> _ChunkMetadata;
                uint _InstanceID;
            #endif
            
            void setup()
            {
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    _InstanceID = UNITY_GET_INSTANCE_ID(v);
                #endif
            }
            
            Varyings ShadowPassVertex(Attributes input, uint vertexID : SV_VertexID)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                
                float3 positionWS;
                float3 normalWS;
                
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    ChunkMetadata chunk = _ChunkMetadata[_InstanceID];
                    uint poolIndex = chunk.vertexOffset + vertexID;
                    
                    positionWS = _VertexPool[poolIndex].xyz;
                    normalWS = _NormalPool[poolIndex].xyz;
                #else
                    positionWS = TransformObjectToWorld(input.positionOS.xyz);
                    normalWS = TransformObjectToWorldNormal(input.normalOS);
                #endif
                
                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _MainLightPosition.xyz));
                
                return output;
            }
            
            half4 ShadowPassFragment(Varyings input) : SV_TARGET
            {
                return 0;
            }
            ENDHLSL
        }
    }
    
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}