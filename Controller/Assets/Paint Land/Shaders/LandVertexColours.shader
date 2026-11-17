// The land's material (URP): colours from the mesh's vertex colours (painted by Tools > Paint Land > Rebuild Land, or
// by hand with Polybrush), optionally with a texture such as grass laid over the top, and a faceted, softly lit look
// like Astroneer's. The texture is projected from above and from the sides, so the land needs no UVs for it.
// Over that, up to 8 textures painted on with Tools > Paint Land > Textures: with a soft brush (how much of
// each is kept in two textures laid over the land from above, 4 textures each) or a whole triangle at a time (kept in the
// land's meshes). Each texture is projected from above and the sides too, at its own size, and can take on the land's
// colour. The ground's colours painted by hand (Paint Ground) are mixed into its vertex colours (kept in a stream of their
// own). Then the colours painted with Paint Land: whole triangles coloured in (kept in the land's meshes, one colour a
// triangle) and paths (kept in a texture laid over the land from above, so a path can be any width, with crisp edges,
// whatever size the triangles are).
Shader "Paint Land/Land (Vertex Colours)"
{
    Properties
    {
        [Header(Colour)]
        [MainColor] _BaseColor("Tint", Color) = (1, 1, 1, 1)
        _VertexColors("Vertex Colours", Range(0, 1)) = 1

        [Header(Textures painted with Paint Land)]
        _TextureEdge("Texture Edges (blended to crisp)", Range(0, 1)) = 0
        [NoScaleOffset] _TextureMap1("Where Textures 1-4 Are", 2D) = "black" {}
        [NoScaleOffset] _TextureMap2("Where Textures 5-8 Are", 2D) = "black" {}
        [NoScaleOffset] _Layer1("Texture 1", 2D) = "grey" {}
        _Layer1Size("Texture 1 Size (metres)", Float) = 4
        _Layer1Tint("Texture 1 Tint", Color) = (1, 1, 1, 1)
        _Layer1Land("Texture 1 Land Colour (0 = its own)", Range(0, 1)) = 0
        _Layer1Grass("Texture 1 Grass On It (Grass Field)", Range(0, 1)) = 1
        [NoScaleOffset] _Layer2("Texture 2", 2D) = "grey" {}
        _Layer2Size("Texture 2 Size (metres)", Float) = 4
        _Layer2Tint("Texture 2 Tint", Color) = (1, 1, 1, 1)
        _Layer2Land("Texture 2 Land Colour (0 = its own)", Range(0, 1)) = 0
        _Layer2Grass("Texture 2 Grass On It (Grass Field)", Range(0, 1)) = 1
        [NoScaleOffset] _Layer3("Texture 3", 2D) = "grey" {}
        _Layer3Size("Texture 3 Size (metres)", Float) = 4
        _Layer3Tint("Texture 3 Tint", Color) = (1, 1, 1, 1)
        _Layer3Land("Texture 3 Land Colour (0 = its own)", Range(0, 1)) = 0
        _Layer3Grass("Texture 3 Grass On It (Grass Field)", Range(0, 1)) = 1
        [NoScaleOffset] _Layer4("Texture 4", 2D) = "grey" {}
        _Layer4Size("Texture 4 Size (metres)", Float) = 4
        _Layer4Tint("Texture 4 Tint", Color) = (1, 1, 1, 1)
        _Layer4Land("Texture 4 Land Colour (0 = its own)", Range(0, 1)) = 0
        _Layer4Grass("Texture 4 Grass On It (Grass Field)", Range(0, 1)) = 1
        [NoScaleOffset] _Layer5("Texture 5", 2D) = "grey" {}
        _Layer5Size("Texture 5 Size (metres)", Float) = 4
        _Layer5Tint("Texture 5 Tint", Color) = (1, 1, 1, 1)
        _Layer5Land("Texture 5 Land Colour (0 = its own)", Range(0, 1)) = 0
        _Layer5Grass("Texture 5 Grass On It (Grass Field)", Range(0, 1)) = 1
        [NoScaleOffset] _Layer6("Texture 6", 2D) = "grey" {}
        _Layer6Size("Texture 6 Size (metres)", Float) = 4
        _Layer6Tint("Texture 6 Tint", Color) = (1, 1, 1, 1)
        _Layer6Land("Texture 6 Land Colour (0 = its own)", Range(0, 1)) = 0
        _Layer6Grass("Texture 6 Grass On It (Grass Field)", Range(0, 1)) = 1
        [NoScaleOffset] _Layer7("Texture 7", 2D) = "grey" {}
        _Layer7Size("Texture 7 Size (metres)", Float) = 4
        _Layer7Tint("Texture 7 Tint", Color) = (1, 1, 1, 1)
        _Layer7Land("Texture 7 Land Colour (0 = its own)", Range(0, 1)) = 0
        _Layer7Grass("Texture 7 Grass On It (Grass Field)", Range(0, 1)) = 1
        [NoScaleOffset] _Layer8("Texture 8", 2D) = "grey" {}
        _Layer8Size("Texture 8 Size (metres)", Float) = 4
        _Layer8Tint("Texture 8 Tint", Color) = (1, 1, 1, 1)
        _Layer8Land("Texture 8 Land Colour (0 = its own)", Range(0, 1)) = 0
        _Layer8Grass("Texture 8 Grass On It (Grass Field)", Range(0, 1)) = 1

        [Header(Texture (optional))]
        [MainTexture] _BaseMap("Texture", 2D) = "white" {}
        _TextureStrength("Texture Strength", Range(0, 1)) = 0
        _TextureSize("Texture Size (metres)", Float) = 6

        [Header(Look)]
        [Toggle(_SMOOTH_SHADING)] _SmoothShading("Smooth Shading (off = faceted)", Float) = 0
        _SoftLight("Soft Light", Range(0, 1)) = 0.35

        [Header(Paths painted with Paint Land)]
        [NoScaleOffset] _PathMap("Paths", 2D) = "black" {}
        _PathEdge("Path Edges (crisp to soft)", Range(0, 1)) = 0
        [HideInInspector] _PathRect("Path Area", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _VertexColors;
            half _TextureStrength;
            float _TextureSize;
            half _SmoothShading;
            half _SoftLight;
            float4 _PathRect;           // where the paths texture lies: (left x, back z, 1 / width, 1 / depth), in the land's space
            float4 _PathMap_TexelSize;
            half _PathEdge;
            half _TextureEdge;
            float _Layer1Size; half4 _Layer1Tint; half _Layer1Land; half _Layer1Grass;
            float _Layer2Size; half4 _Layer2Tint; half _Layer2Land; half _Layer2Grass;
            float _Layer3Size; half4 _Layer3Tint; half _Layer3Land; half _Layer3Grass;
            float _Layer4Size; half4 _Layer4Tint; half _Layer4Land; half _Layer4Grass;
            float _Layer5Size; half4 _Layer5Tint; half _Layer5Land; half _Layer5Grass;
            float _Layer6Size; half4 _Layer6Tint; half _Layer6Land; half _Layer6Grass;
            float _Layer7Size; half4 _Layer7Tint; half _Layer7Land; half _Layer7Grass;
            float _Layer8Size; half4 _Layer8Tint; half _Layer8Land; half _Layer8Grass;
        CBUFFER_END

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_PathMap);
        SAMPLER(sampler_PathMap);
        TEXTURE2D(_TextureMap1);
        SAMPLER(sampler_TextureMap1);
        TEXTURE2D(_TextureMap2);
        TEXTURE2D(_Layer1);
        TEXTURE2D(_Layer2);
        TEXTURE2D(_Layer3);
        TEXTURE2D(_Layer4);
        TEXTURE2D(_Layer5);
        TEXTURE2D(_Layer6);
        TEXTURE2D(_Layer7);
        TEXTURE2D(_Layer8);
        SAMPLER(sampler_trilinear_repeat);   // (shared by the 8 textures)

        // The flat normal of the triangle being drawn (the faceted look), facing the same way as the smooth one
        float3 FacetNormal(float3 positionWS, float3 smoothNormalWS)
        {
            float3 n = normalize(cross(ddy(positionWS), ddx(positionWS)));
            return dot(n, smoothNormalWS) < 0.0 ? -n : n;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma shader_feature_local_fragment _SMOOTH_SHADING
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
                half4 trianglePaint : TEXCOORD2;    // the colour painted on this vertex's triangle (alpha = how much)
                half4 triangleTexture : TEXCOORD3;  // the texture painted on its triangle: x = which (1-8, /255; 0 = none), y = how much
                half4 groundPaint : TEXCOORD4;      // the ground's colour painted by hand here (Paint Ground): colour times how much, how much
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                half3 vertexLight : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                float2 pathUV : TEXCOORD4;
                half4 trianglePaint : TEXCOORD5;
                half4 triangleTexture : TEXCOORD6;
                half4 groundPaint : TEXCOORD7;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // How much path there is here: the painted amount, its edge made crisp (and kept smooth, not pixelated, however
            // close you look), and thin paths kept from fading out in the distance
            half PathAmount(float2 uv, half painted)
            {
                float2 texel = uv * _PathMap_TexelSize.zw;
                float2 dx = ddx(texel), dy = ddy(texel);
                float lod = 0.5 * log2(max(max(dot(dx, dx), dot(dy, dy)), 1e-8));
                half edge = 0.5 / (1.0 + max(lod, 0.0) * 0.6);
                half blur = max(fwidth(painted), 0.001) * 0.75;
                half crisp = smoothstep(edge - blur, edge + blur, painted) * step(0.002, painted);
                half inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0) * step(1e-7, _PathRect.z);
                return lerp(crisp, painted, _PathEdge) * inside;
            }

            // One painted texture here: projected from above and the sides (by how the ground faces), at its size, its own
            // colours or (Land Colour) the land's colour with the texture's light and dark in it, then tinted
            half3 LayerColour(TEXTURE2D_PARAM(layer, layerSampler), float size, half4 tint, half land,
                              float3 p, float3 dpdx, float3 dpdy, float3 blend, half3 ground)
            {
                float s = 1.0 / max(size, 0.01);
                half3 c = 0;
                [branch] if (blend.y > 0.0) c += SAMPLE_TEXTURE2D_GRAD(layer, layerSampler, p.xz * s, dpdx.xz * s, dpdy.xz * s).rgb * blend.y;
                [branch] if (blend.x > 0.0) c += SAMPLE_TEXTURE2D_GRAD(layer, layerSampler, p.zy * s, dpdx.zy * s, dpdy.zy * s).rgb * blend.x;
                [branch] if (blend.z > 0.0) c += SAMPLE_TEXTURE2D_GRAD(layer, layerSampler, p.xy * s, dpdx.xy * s, dpdy.xy * s).rgb * blend.z;
                half3 average = SAMPLE_TEXTURE2D_LOD(layer, layerSampler, float2(0.5, 0.5), 16).rgb;   // (its smallest mip)
                half3 luma = half3(0.2126, 0.7152, 0.0722);
                half3 onLand = ground * (dot(c, luma) / max(dot(average, luma), 0.02));
                return lerp(c, onLand, land) * tint.rgb;
            }

            // The textures painted with Paint Land over the ground's colour: how much of each from the two textures laid over
            // the land (blended, or made crisp by Texture Edges), and a texture painted on the whole triangle over that
            half3 PaintedTextures(half3 ground, float2 uv, float3 positionWS, float3 smoothNormalWS, half4 triangleTexture)
            {
                half inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0) * step(1e-7, _PathRect.z);
                half4 w1 = SAMPLE_TEXTURE2D(_TextureMap1, sampler_TextureMap1, uv) * inside;
                half4 w2 = SAMPLE_TEXTURE2D(_TextureMap2, sampler_TextureMap1, uv) * inside;
                half4 blur1 = max(fwidth(w1), 0.001) * 0.75, blur2 = max(fwidth(w2), 0.001) * 0.75;
                w1 = lerp(w1, smoothstep(0.5 - blur1, 0.5 + blur1, w1), _TextureEdge);
                w2 = lerp(w2, smoothstep(0.5 - blur2, 0.5 + blur2, w2), _TextureEdge);

                half index = round(triangleTexture.x * 255.0);
                half amount = saturate(triangleTexture.y) * step(0.5, index);
                w1 = w1 * (1.0 - amount) + step(abs(index - half4(1, 2, 3, 4)), 0.5) * amount;
                w2 = w2 * (1.0 - amount) + step(abs(index - half4(5, 6, 7, 8)), 0.5) * amount;
                half total = dot(w1, 1.0) + dot(w2, 1.0);
                half scale = 1.0 / max(total, 1.0);
                w1 *= scale;
                w2 *= scale;
                total *= scale;

                float3 dpdx = ddx(positionWS), dpdy = ddy(positionWS);   // (worked out here, not in the branches below)
                float3 blend = pow(abs(smoothNormalWS), 4.0);
                blend /= max(blend.x + blend.y + blend.z, 1e-4);
                blend *= step(0.02, blend);
                blend /= max(blend.x + blend.y + blend.z, 1e-4);
                [branch] if (total < 0.002) return ground;

                half3 colour = 0;
                [branch] if (w1.x > 0.002) colour += w1.x * LayerColour(TEXTURE2D_ARGS(_Layer1, sampler_trilinear_repeat), _Layer1Size, _Layer1Tint, _Layer1Land, positionWS, dpdx, dpdy, blend, ground);
                [branch] if (w1.y > 0.002) colour += w1.y * LayerColour(TEXTURE2D_ARGS(_Layer2, sampler_trilinear_repeat), _Layer2Size, _Layer2Tint, _Layer2Land, positionWS, dpdx, dpdy, blend, ground);
                [branch] if (w1.z > 0.002) colour += w1.z * LayerColour(TEXTURE2D_ARGS(_Layer3, sampler_trilinear_repeat), _Layer3Size, _Layer3Tint, _Layer3Land, positionWS, dpdx, dpdy, blend, ground);
                [branch] if (w1.w > 0.002) colour += w1.w * LayerColour(TEXTURE2D_ARGS(_Layer4, sampler_trilinear_repeat), _Layer4Size, _Layer4Tint, _Layer4Land, positionWS, dpdx, dpdy, blend, ground);
                [branch] if (w2.x > 0.002) colour += w2.x * LayerColour(TEXTURE2D_ARGS(_Layer5, sampler_trilinear_repeat), _Layer5Size, _Layer5Tint, _Layer5Land, positionWS, dpdx, dpdy, blend, ground);
                [branch] if (w2.y > 0.002) colour += w2.y * LayerColour(TEXTURE2D_ARGS(_Layer6, sampler_trilinear_repeat), _Layer6Size, _Layer6Tint, _Layer6Land, positionWS, dpdx, dpdy, blend, ground);
                [branch] if (w2.z > 0.002) colour += w2.z * LayerColour(TEXTURE2D_ARGS(_Layer7, sampler_trilinear_repeat), _Layer7Size, _Layer7Tint, _Layer7Land, positionWS, dpdx, dpdy, blend, ground);
                [branch] if (w2.w > 0.002) colour += w2.w * LayerColour(TEXTURE2D_ARGS(_Layer8, sampler_trilinear_repeat), _Layer8Size, _Layer8Tint, _Layer8Land, positionWS, dpdx, dpdy, blend, ground);
                return ground * (1.0 - total) + colour;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.vertexLight = VertexLighting(positions.positionWS, output.normalWS);
                output.fogFactor = ComputeFogFactor(positions.positionCS.z);
                output.pathUV = (input.positionOS.xz - _PathRect.xy) * _PathRect.zw;
                output.trianglePaint = input.trianglePaint;
                output.triangleTexture = input.triangleTexture;
                output.groundPaint = input.groundPaint;
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 smoothNormalWS = normalize(input.normalWS);
                #if defined(_SMOOTH_SHADING)
                    float3 normalWS = smoothNormalWS;
                #else
                    float3 normalWS = FacetNormal(input.positionWS, smoothNormalWS);
                #endif

                // The ground's colour: the vertex colours (painted as sRGB colours) times the texture, projected from above
                // and the sides by how the ground faces (the smooth normal, so it doesn't change from facet to facet)
                half3 vertexColor = input.color.rgb * (1.0 - saturate(input.groundPaint.a)) + input.groundPaint.rgb;   // (with Paint Ground's colours)
                half3 triangleColor = input.trianglePaint.rgb;
                #if !defined(UNITY_COLORSPACE_GAMMA)
                    vertexColor = SRGBToLinear(vertexColor);
                    triangleColor = SRGBToLinear(triangleColor);
                #endif
                float3 blend = pow(abs(smoothNormalWS), 4.0);
                blend /= max(blend.x + blend.y + blend.z, 1e-4);
                float3 p = input.positionWS / max(_TextureSize, 0.01);
                half3 projected = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, p.zy * _BaseMap_ST.xy + _BaseMap_ST.zw).rgb * blend.x
                              + SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, p.xz * _BaseMap_ST.xy + _BaseMap_ST.zw).rgb * blend.y
                              + SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, p.xy * _BaseMap_ST.xy + _BaseMap_ST.zw).rgb * blend.z;
                half3 albedo = _BaseColor.rgb * lerp(half3(1, 1, 1), vertexColor, _VertexColors)
                                              * lerp(half3(1, 1, 1), projected, _TextureStrength);

                // Painted on top (Tools > Paint Land): textures, coloured triangles, then paths
                albedo = PaintedTextures(albedo, input.pathUV, input.positionWS, smoothNormalWS, input.triangleTexture);
                albedo = lerp(albedo, triangleColor, saturate(input.trianglePaint.a));
                half4 path = SAMPLE_TEXTURE2D(_PathMap, sampler_PathMap, input.pathUV);
                albedo = lerp(albedo, path.rgb, PathAmount(input.pathUV, path.a));

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
                inputData.vertexLighting = input.vertexLight;
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.alpha = 1;
                surface.occlusion = 1;
                half4 color = UniversalFragmentBlinnPhong(inputData, surface);

                // Soft light: the sun wraps a little round onto the shaded side, for a soft, bright look
                Light sun = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
                half facing = dot(normalWS, sun.direction);
                half wrap = saturate(facing * 0.5 + 0.5) - saturate(facing);
                color.rgb += albedo * sun.color * (wrap * _SoftLight * (0.5 + 0.5 * sun.shadowAttenuation));

                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                return half4(color.rgb, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 ShadowVert(ShadowAttributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            half4 ShadowFrag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 DepthVert(DepthAttributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return TransformObjectToHClip(input.positionOS.xyz);
            }

            half4 DepthFrag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex NormalsVert
            #pragma fragment NormalsFrag
            #pragma shader_feature_local_fragment _SMOOTH_SHADING
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing

            struct NormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct NormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            NormalsVaryings NormalsVert(NormalsAttributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                NormalsVaryings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 NormalsFrag(NormalsVaryings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                #if !defined(_SMOOTH_SHADING)
                    normalWS = FacetNormal(input.positionWS, normalWS);
                #endif
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

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
