// Lit by the main light, coloured per instance. The alpha of the instance colour is how much of the colour is added
// as emission, which is what the bloom picks up.
Shader "Demo/InstancedColor"
{
    Properties
    {
        _Ambient ("Ambient", Range(0, 1)) = 0.3
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Ambient;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(PerInstance)
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstanceColor)
            UNITY_INSTANCING_BUFFER_END(PerInstance)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float4 color : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.color = UNITY_ACCESS_INSTANCED_PROP(PerInstance, _InstanceColor);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                Light light = GetMainLight();
                float3 normal = normalize(input.normalWS);
                float diffuse = saturate(dot(normal, light.direction));
                // A little light from below too, so the unlit side keeps its shape.
                float fill = 0.25 * saturate(dot(normal, -light.direction));
                float3 lit = input.color.rgb * (light.color * diffuse + _Ambient + fill);
                float3 emission = input.color.rgb * input.color.a;
                return half4(lit + emission, 1);
            }
            ENDHLSL
        }
    }
}
