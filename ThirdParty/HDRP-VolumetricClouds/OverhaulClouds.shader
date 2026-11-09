// Overhaul weather, clouds: HDRP's volumetric clouds ported to the built-in pipeline (HDRP/VolumetricCloudsPort.hlsl, Unity
// Companion License, see HDRP/README.txt), "simple preset" path: a layer around the planet, shaped by HDRP's Worley and
// Perlin noise volumes and a 64x1 height profile (density, erosion, ambient occlusion) given by the mod per weather.
// Pass 0: traced at half resolution into a target (rgb light, a = 1 - transmittance), pass 1: laid over the sky pixels.
Shader "Hidden/Overhaul/Clouds"
{
    CGINCLUDE
    #include "UnityCG.cginc"
    float4 _CloudCorner, _CloudRight, _CloudUp;
    float3 _CamPos;
    float _Fade;

    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
    v2f vert(uint id : SV_VertexID)
    {
        v2f o; float2 uv = float2((id << 1) & 2, id & 2);
        o.pos = float4(uv * 2 - 1, 0, 1); o.uv = uv;
        if (_ProjectionParams.x < 0) o.uv.y = 1 - o.uv.y;
        return o;
    }

    ENDCG

    SubShader
    {
        ZWrite Off ZTest Always Cull Off
        // 0: the clouds, low resolution, traced with the HDRP port (camera-relative: camera at (0, height, 0))
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "HDRP/VolumetricCloudsPort.hlsl"
            float4 frag(v2f i) : SV_Target
            {
                CloudRay ray;
                ray.originWS = float3(0, _CamPos.y, 0);
                ray.direction = normalize(_CloudCorner.xyz + _CloudRight.xyz * i.uv.x + _CloudUp.xyz * i.uv.y);
                ray.maxRayLength = 200000;
                // Interleaved gradient noise (stable per pixel, no flicker without temporal accumulation).
                float2 px = i.uv * _ScreenParams.xy;
                ray.integrationNoise = frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715))));
                ray.envLighting = (EnvironmentLighting)0;
                VolumetricRayResult r = TraceVolumetricRay(ray);
                float a = (1 - r.transmittance) * _Fade;
                return float4(r.scattering * _Fade, a);
            }
            ENDCG
        }
        // 1: composite over the sky pixels
        Pass
        {
            Blend One OneMinusSrcAlpha
            ColorMask RGB
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            sampler2D _CloudTex;
            float4 frag(v2f i) : SV_Target
            {
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                #if UNITY_REVERSED_Z
                if (raw > 0.0000001) discard;
                #else
                if (raw < 0.9999999) discard;
                #endif
                return tex2D(_CloudTex, i.uv);
            }
            ENDCG
        }
    }
}
