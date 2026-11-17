// The Grass Field's grass (URP): thousands of grass tufts drawn at once by GrassField (no objects), each one placed from a
// list on the GPU (_Instances, picked by SV_InstanceID): where, how big, which way, how it leans, and its colour (the
// land's colour where it grows). Lit like the land (the ground's own flat facet, the same soft light), so it sits in the
// ground; darker at the roots and a little lighter at the tips; swaying in the wind; shrinking away into the ground at the
// edge of the grass distance instead of popping. Only the texture's alpha is used (the blades' shape).
Shader "Paint Land/Grass Field"
{
    Properties
    {
        [MainTexture] _BaseMap("Blades (alpha)", 2D) = "white" {}
        _Cutoff("Blade Thickness Cutoff", Range(0, 1)) = 0.5
        _Gradient("Gradient", Range(0.1, 5)) = 1.4
        _TipBrightness("Tip Brightness", Range(0.5, 2)) = 1.1
        _BottomShade("Darker At Bottom", Range(0, 1)) = 0.35
        _SoftLight("Soft Light", Range(0, 1)) = 0.35
        _WindStrength("Wind Strength", Range(0, 1)) = 0.15
        _WindSpeed("Wind Speed", Range(0, 5)) = 1.2
        _WindDirection("Wind Direction", Vector) = (1, 0, 0.3, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half _Cutoff;
            half _Gradient;
            half _TipBrightness;
            half _BottomShade;
            half _SoftLight;
            half _WindStrength;
            half _WindSpeed;
            float4 _WindDirection;
        CBUFFER_END

        // Set for each lot drawn (by GrassField)
        float _FadeStart;
        float _FadeEnd;

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        // One tuft (48 bytes, as GrassField.Tuft)
        struct Tuft
        {
            float3 position;    // where its roots are, in the world
            float scale;
            float yaw;          // which way it's turned (radians)
            float stretch;      // taller (> 1) or shorter than wide
            float2 lean;        // how far its top leans, per metre of height (x, z)
            float3 normal;      // the ground's facet there (for the light)
            uint colour;        // sRGB, 8 bits each: r, g, b
        };
        StructuredBuffer<Tuft> _Instances;

        // A vertex of the tuft mesh, placed for one tuft
        float3 PlaceVertex(float3 v, Tuft tuft)
        {
            float distanceToCamera = distance(_WorldSpaceCameraPos.xyz, tuft.position);
            float fade = saturate((_FadeEnd - distanceToCamera) / max(_FadeEnd - _FadeStart, 0.001));
            v.y *= tuft.stretch;
            v *= tuft.scale * fade;
            float s, c;
            sincos(tuft.yaw, s, c);
            v.xz = float2(v.x * c - v.z * s, v.x * s + v.z * c);

            // Lean and wind bend it more toward the top
            float time = _Time.y * _WindSpeed;
            float phase = dot(tuft.position.xz, float2(0.35, 0.27));
            float gust = sin(time + phase) * 0.6 + sin(time * 1.73 + phase * 1.9) * 0.4;
            float2 wind = normalize(_WindDirection.xz + float2(1e-5, 0.0)) * (gust * _WindStrength);
            v.xz += (tuft.lean + wind) * max(v.y, 0.0);
            return tuft.position + v;
        }

        half3 TuftColour(Tuft tuft)
        {
            half3 colour = half3(tuft.colour & 255u, (tuft.colour >> 8) & 255u, (tuft.colour >> 16) & 255u) / 255.0;
            #if !defined(UNITY_COLORSPACE_GAMMA)
                colour = SRGBToLinear(colour);
            #endif
            return colour;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                nointerpolation half3 colour : TEXCOORD2;
                nointerpolation float3 normalWS : TEXCOORD3;
                half height : TEXCOORD4;
                half fogFactor : TEXCOORD5;
            };

            Varyings Vert(Attributes input, uint instanceID : SV_InstanceID)
            {
                Tuft tuft = _Instances[instanceID];
                Varyings output = (Varyings)0;
                output.positionWS = PlaceVertex(input.positionOS.xyz, tuft);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.colour = TuftColour(tuft);
                output.normalWS = tuft.normal;
                output.height = saturate(input.uv.y * _Gradient);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a;
                clip(alpha - _Cutoff);

                half3 albedo = input.colour * lerp(1.0 - _BottomShade, _TipBrightness, input.height);
                float3 normalWS = normalize(input.normalWS);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    inputData.shadowCoord = ComputeScreenPos(TransformWorldToHClip(input.positionWS));
                #else
                    inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #endif
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.alpha = 1;
                surface.occlusion = 1;
                half4 colour = UniversalFragmentBlinnPhong(inputData, surface);

                // The land's soft light, so the grass is lit just like the ground under it
                Light sun = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
                half facing = dot(normalWS, sun.direction);
                half wrap = saturate(facing * 0.5 + 0.5) - saturate(facing);
                colour.rgb += albedo * sun.color * (wrap * _SoftLight * (0.5 + 0.5 * sun.shadowAttenuation));

                colour.rgb = MixFog(colour.rgb, inputData.fogCoord);
                return half4(colour.rgb, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            DepthVaryings DepthVert(DepthAttributes input, uint instanceID : SV_InstanceID)
            {
                Tuft tuft = _Instances[instanceID];
                DepthVaryings output;
                output.positionCS = TransformWorldToHClip(PlaceVertex(input.positionOS.xyz, tuft));
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 DepthFrag(DepthVaryings input) : SV_Target
            {
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex NormalsVert
            #pragma fragment NormalsFrag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            struct NormalsAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct NormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                nointerpolation float3 normalWS : TEXCOORD1;
            };

            NormalsVaryings NormalsVert(NormalsAttributes input, uint instanceID : SV_InstanceID)
            {
                Tuft tuft = _Instances[instanceID];
                NormalsVaryings output;
                output.positionCS = TransformWorldToHClip(PlaceVertex(input.positionOS.xyz, tuft));
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.normalWS = tuft.normal;
                return output;
            }

            half4 NormalsFrag(NormalsVaryings input) : SV_Target
            {
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a - _Cutoff);
                float3 normalWS = normalize(input.normalWS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 octNormal = PackNormalOctQuadEncode(normalWS);
                    return half4(PackFloat2To888(saturate(octNormal * 0.5 + 0.5)), 0.0);
                #else
                    return half4(normalWS, 0.0);
                #endif
            }
            ENDHLSL
        }
    }

    FallBack Off
}
