// Water (URP), for World/Water (LandWater): see-through and light where it's shallow, a deeper colour further down (from
// how far it is to the ground under it, read from the camera's depth texture), white foam where it meets the land, gentle
// waves moving across it (worked out here, no textures needed), the sun shining on it and the sky reflected at a low angle.
// Painted water (Paint Land > Water's brush) is cut to where it's been painted (_WaterMask, set by LandWater), with a
// line of foam lapping along that painted edge.
// It needs the camera's depth texture (Depth Texture on the URP asset: Paint Land > Water turns it on); without it the
// water is just its deep colour, with no foam.
Shader "Paint Land/Water"
{
    Properties
    {
        [Header(Colour)]
        _ShallowColor("Shallow Colour (alpha = how see-through)", Color) = (0.36, 0.8, 0.82, 0.45)
        _DeepColor("Deep Colour", Color) = (0.07, 0.34, 0.6, 0.92)
        _Depth("Depth To Deep Colour (m)", Range(0.1, 50)) = 6

        [Header(Shore)]
        _FoamColor("Foam (alpha = how much)", Color) = (1, 1, 1, 0.9)
        _FoamWidth("Foam Width (m)", Range(0, 4)) = 0.6

        [Header(Waves)]
        _WaveStrength("Waves", Range(0, 1)) = 0.35
        _WaveSize("Wave Size (m)", Range(0.5, 50)) = 6
        _WaveSpeed("Wave Speed", Range(0, 4)) = 0.8

        [Header(Light)]
        _Shine("Shine", Range(0, 1)) = 0.8
        _Reflection("Sky Reflection", Range(0, 1)) = 0.4

        [HideInInspector] [NoScaleOffset] _WaterMask("Painted Area", 2D) = "white" {}
        [HideInInspector] _MaskOn("Painted", Float) = 0
        [HideInInspector] _MaskSize("Painted Area Size", Vector) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                float _Depth;
                half4 _FoamColor;
                float _FoamWidth;
                half _WaveStrength;
                float _WaveSize;
                half _WaveSpeed;
                half _Shine;
                half _Reflection;
                half _MaskOn;
                float4 _MaskSize;   // (metres across x, z; spots across x, z)
            CBUFFER_END

            TEXTURE2D(_WaterMask);
            SAMPLER(sampler_WaterMask);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half fogFactor : TEXCOORD1;
                float2 uv : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                output.uv = input.uv;
                return output;
            }

            // How the surface slopes (x, z) at a point: a few waves crossing each other, moving with time
            float2 WaveSlope(float2 p, float t)
            {
                const float2 d1 = float2(0.8, 0.6), d2 = float2(-0.6, 0.8), d3 = float2(0.2, -0.98), d4 = float2(-0.92, -0.39);
                float2 slope = d1 * cos(dot(p, d1) * 1.0 + t * 1.1);
                slope += d2 * cos(dot(p, d2) * 1.7 + t * 1.5) * 0.6;
                slope += d3 * cos(dot(p, d3) * 2.9 + t * 2.1) * 0.35;
                slope += d4 * cos(dot(p, d4) * 4.3 + t * 2.7) * 0.2;
                return slope;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 positionWS = input.positionWS;
                float3 viewDir = GetWorldSpaceNormalizeViewDir(positionWS);

                // How deep the water is here: from the surface to the ground seen through it
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float surfaceEye = -TransformWorldToView(positionWS).z;
                float thickness = max(sceneEye - surfaceEye, 0.0);
                float depthBelow = thickness * max(abs(viewDir.y), 0.2);

                // Waves
                float t = _Time.y * _WaveSpeed;
                float2 slope = WaveSlope(positionWS.xz / max(_WaveSize, 0.1), t) * (_WaveStrength * 0.25);
                float3 normalWS = normalize(float3(-slope.x, 1.0, -slope.y));

                // Colour: shallow to deep, lit by the sun and the sky
                half deep = saturate(depthBelow / max(_Depth, 0.01));
                half4 water = lerp(_ShallowColor, _DeepColor, deep * (2.0 - deep));
                Light sun = GetMainLight(TransformWorldToShadowCoord(positionWS));
                half shadow = sun.shadowAttenuation;
                half3 ambient = SampleSH(normalWS);
                half diffuse = saturate(dot(normalWS, sun.direction)) * 0.6 + 0.4;
                half3 colour = water.rgb * (sun.color * diffuse * lerp(0.55, 1.0, shadow) + ambient);

                // The sky reflected at a low angle, and the sun's shine
                half fresnel = pow(1.0 - saturate(dot(normalWS, viewDir)), 4.0);
                half3 sky = SampleSH(reflect(-viewDir, normalWS));
                colour = lerp(colour, sky, saturate(fresnel * _Reflection * 1.5));
                float3 halfDir = normalize(sun.direction + viewDir);
                half spec = pow(saturate(dot(normalWS, halfDir)), lerp(16.0, 512.0, _Shine)) * _Shine * 2.0;
                colour += sun.color * spec * shadow;
                half alpha = lerp(water.a, 1.0, fresnel * 0.6);

                // Foam where it meets the land
                half edge = 1.0 - saturate(depthBelow / max(_FoamWidth, 0.001));
                half ripple = sin((positionWS.x + positionWS.z) * 1.3 + t * 2.0) * 0.5 + 0.5;
                half foam = smoothstep(0.45, 0.6, edge - ripple * 0.15) * _FoamColor.a * step(0.001, _FoamWidth);
                colour = lerp(colour, _FoamColor.rgb * (sun.color * lerp(0.6, 1.0, shadow) + ambient), foam);
                alpha = max(alpha, foam);
                alpha *= saturate(thickness * 8.0);   // (no hard line where it meets the ground)

                // Painted water: only where it's painted (a crisp, smooth edge), with foam lapping along that edge
                half painted = SAMPLE_TEXTURE2D(_WaterMask, sampler_WaterMask, input.uv).r;
                UNITY_BRANCH
                if (_MaskOn > 0.5 && _FoamWidth > 0.001)
                {
                    // How far in from the painted edge this is (metres): from how fast the paint changes round here
                    float2 texel = 1.0 / max(_MaskSize.zw, 1.0);
                    float px = SAMPLE_TEXTURE2D_LOD(_WaterMask, sampler_WaterMask, input.uv + float2(texel.x, 0), 0).r
                             - SAMPLE_TEXTURE2D_LOD(_WaterMask, sampler_WaterMask, input.uv - float2(texel.x, 0), 0).r;
                    float pz = SAMPLE_TEXTURE2D_LOD(_WaterMask, sampler_WaterMask, input.uv + float2(0, texel.y), 0).r
                             - SAMPLE_TEXTURE2D_LOD(_WaterMask, sampler_WaterMask, input.uv - float2(0, texel.y), 0).r;
                    float2 perMetre = float2(px, pz) / max(2.0 * texel * _MaskSize.xy, 1e-4);
                    float inFromEdge = (painted - 0.5) / max(length(perMetre), 1e-3);
                    half lap = sin(inFromEdge * 5.0 - t * 2.2 + (positionWS.x - positionWS.z) * 0.35) * 0.5 + 0.5;
                    half band = 1.0 - saturate(inFromEdge / (_FoamWidth * 1.3));
                    half edgeFoam = smoothstep(0.35, 0.7, band - lap * 0.3) * _FoamColor.a;
                    colour = lerp(colour, _FoamColor.rgb * (sun.color * lerp(0.6, 1.0, shadow) + ambient), edgeFoam);
                    alpha = max(alpha, edgeFoam);
                }
                half blur = max(fwidth(painted), 0.001) * 0.75;
                alpha *= lerp(1.0, smoothstep(0.5 - blur, 0.5 + blur, painted), _MaskOn);

                colour = MixFog(colour, input.fogFactor);
                return half4(colour, saturate(alpha));
            }
            ENDHLSL
        }
    }

    FallBack Off
}
