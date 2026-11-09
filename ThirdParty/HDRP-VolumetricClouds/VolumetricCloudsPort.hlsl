// Ported from Unity's High Definition Render Pipeline, Runtime/Lighting/VolumetricClouds/VolumetricCloudsUtilities.hlsl and
// Runtime/Sky/CloudUtils.hlsl (com.unity.render-pipelines.high-definition), copyright (c) 2020 Unity Technologies ApS,
// Unity Companion License (see LICENSE-Unity-Companion.md next to this file). Adapted to Unity's built-in render pipeline
// for a Valheim mod: "simple preset" path only (the height profile LUT is a 64x1 texture given by the mod, the coverage a
// constant), the ambient probe replaced by two colours (top/bottom of the layer), no exposure, no XR, plain samplers.
#ifndef OVERHAUL_VOLUMETRIC_CLOUDS_PORT
#define OVERHAUL_VOLUMETRIC_CLOUDS_PORT

#define NUM_MULTI_SCATTERING_OCTAVES 2
#define CLOUD_DENSITY_TRESHOLD 0.001f
#define EMPTY_STEPS_BEFORE_LARGE_STEPS 8
#define FORWARD_ECCENTRICITY 0.7
#define BACKWARD_ECCENTRICITY 0.7
#define MIN_EROSION_DISTANCE 3000.0
#define MAX_EROSION_DISTANCE 100000.0
#define NOISE_TEXTURE_NORMALIZATION_FACTOR 100000.0f
#define LIGHT_STEP_MAXIMAL_SIZE 1000.0f
#define CLOUD_FLT_MAX 3.402823466e+38

// Parameters (HDRP's ShaderVariablesClouds, the ones the simple preset uses).
float _LowestCloudAltitude, _HighestCloudAltitude, _EarthRadius;
float3 _PlanetCenterPosition;
float _NumPrimarySteps, _NumLightSteps, _MaxStepSize;
float _MultiScattering, _PowderEffectIntensity, _DensityMultiplier;
float _ShapeFactor, _ShapeScale, _ErosionFactor, _ErosionScale, _ErosionFactorCompensation, _ErosionOcclusion;
float _AltitudeDistortion, _MediumWindSpeed, _SmallWindSpeed, _VerticalShapeWindDisplacement, _VerticalErosionWindDisplacement;
float2 _WindVector, _WindDirection, _ShapeNoiseOffset;
float _VerticalShapeNoiseOffset, _FadeInStart, _FadeInDistance, _CameraSpace, _Coverage;
float3 _ScatteringTint, _SunDirection, _SunLightColor, _AmbientTop, _AmbientBottom;
float2 _CloudCameraXZ; // the camera's world xz (the trace runs camera-relative; HDRP read _WorldSpaceCameraPos)
float Sq(float x) { return x * x; }

sampler3D _Worley128RGBA;
sampler3D _ErosionNoise;
sampler2D _CloudLutTexture;

#define ConvertToPS(x) (x - _PlanetCenterPosition)

float CloudRangeRemap(float a, float b, float x) { return saturate((x - a) / (b - a)); }
float CloudPositivePow(float b, float e) { return pow(max(abs(b), 1e-7), e); }
float HenyeyGreenstein(float g, float c) { float g2 = g * g; return (1 - g2) / (4 * 3.14159265 * pow(max(1 + g2 - 2 * g * c, 1e-5), 1.5)); }

// From CloudUtils.hlsl / PhysicallyBasedSkyCommon: ray against a sphere centred on the planet.
float2 IntersectSphere(float sphereRadius, float cosChi, float radialDistance, float rcpRadialDistance)
{
    float d = Sq(sphereRadius * rcpRadialDistance) - saturate(1 - cosChi * cosChi);
    return (d < 0) ? d : (radialDistance * float2(-cosChi - sqrt(d), -cosChi + sqrt(d)));
}
float ComputeCosineOfHorizonAngle(float r) { float sinHor = _EarthRadius / r; return -sqrt(saturate(1 - sinHor * sinHor)); }

bool IntersectCloudVolume(float3 originPS, float3 dir, float lowerBoundPS, float higherBoundPS, out float tEntry, out float tExit)
{
    bool intersect;
    float radialDistance = length(originPS);
    float rcpRadialDistance = rcp(radialDistance);
    float cosChi = dot(originPS, dir) * rcpRadialDistance;
    float2 tInner = IntersectSphere(lowerBoundPS, cosChi, radialDistance, rcpRadialDistance);
    float2 tOuter = IntersectSphere(higherBoundPS, cosChi, radialDistance, rcpRadialDistance);
    if (tInner.x < 0.0 && tInner.y >= 0.0)
    {
        tEntry = tInner.y; tExit = tOuter.y;
        intersect = cosChi >= ComputeCosineOfHorizonAngle(radialDistance);
    }
    else
    {
        tEntry = max(tOuter.x, 0.0f);
        tExit = tInner.x >= 0.0 ? tInner.x : tOuter.y;
        intersect = tOuter.y >= 0.0f;
    }
    return intersect;
}

bool ExitCloudVolume(float3 originPS, float3 dir, float higherBoundPS, out float tExit)
{
    float radialDistance = length(originPS);
    float cosChi = dot(originPS, dir) * rcp(radialDistance);
    tExit = IntersectSphere(higherBoundPS, cosChi, radialDistance, rcp(radialDistance)).y;
    return cosChi >= ComputeCosineOfHorizonAngle(radialDistance);
}

struct EnvironmentLighting { float3 sunDirection; float cosAngle; float2 phaseFunction; };
struct CloudRay { float3 originWS; float3 direction; float maxRayLength; float integrationNoise; EnvironmentLighting envLighting; };

EnvironmentLighting EvaluateEnvironmentLighting(CloudRay ray)
{
    EnvironmentLighting lighting;
    lighting.sunDirection = _SunDirection;
    lighting.cosAngle = dot(ray.direction, lighting.sunDirection);
    float forwardP = HenyeyGreenstein(FORWARD_ECCENTRICITY * CloudPositivePow(_MultiScattering, 0), lighting.cosAngle);
    float backwardsP = HenyeyGreenstein(-BACKWARD_ECCENTRICITY * CloudPositivePow(_MultiScattering, 0), lighting.cosAngle);
    lighting.phaseFunction[0] = forwardP + backwardsP;
    forwardP = HenyeyGreenstein(FORWARD_ECCENTRICITY * CloudPositivePow(_MultiScattering, 1), lighting.cosAngle);
    backwardsP = HenyeyGreenstein(-BACKWARD_ECCENTRICITY * CloudPositivePow(_MultiScattering, 1), lighting.cosAngle);
    lighting.phaseFunction[1] = forwardP + backwardsP;
    return lighting;
}

float DensityRemap(float x, float a, float b, float c, float d) { return (((x - a) / (b - a)) * (d - c)) + c; }

float PowderEffect(float cloudDensity, float cosAngle, float intensity)
{
    float powderEffect = 1.0 - exp(-cloudDensity * 4.0);
    powderEffect = saturate(powderEffect * 2.0);
    return lerp(1.0, lerp(1.0, powderEffect, smoothstep(0.5, -0.5, cosAngle)), intensity);
}

struct CloudProperties { float density; float ambientOcclusion; float height; float sigmaT; };

float EvaluateNormalizedCloudHeight(float3 positionPS) { return CloudRangeRemap(_LowestCloudAltitude, _HighestCloudAltitude, length(positionPS)); }

float3 AnimateShapeNoisePosition(float3 positionPS)
{
    positionPS.y += (positionPS.x / 3.0f + positionPS.z / 7.0f);
    return positionPS + float3(_WindVector.x, 0.0, _WindVector.y) * _MediumWindSpeed + float3(0.0, _VerticalShapeWindDisplacement, 0.0);
}
float3 AnimateErosionNoisePosition(float3 positionPS)
{
    return positionPS + float3(_WindVector.x, 0.0, _WindVector.y) * _SmallWindSpeed + float3(0.0, _VerticalErosionWindDisplacement, 0.0);
}

void EvaluateCloudProperties(float3 positionPS, float noiseMipOffset, float erosionMipOffset, bool cheapVersion, bool lightSampling, out CloudProperties properties)
{
    properties.density = 0; properties.ambientOcclusion = 1.0; properties.sigmaT = 0;
    properties.height = EvaluateNormalizedCloudHeight(positionPS);
    positionPS.xz += _CloudCameraXZ * _CameraSpace;

    float3 baseNoiseSamplingCoordinates = float3(AnimateShapeNoisePosition(positionPS).xzy / NOISE_TEXTURE_NORMALIZATION_FACTOR) * _ShapeScale - float3(_ShapeNoiseOffset.x, _ShapeNoiseOffset.y, _VerticalShapeNoiseOffset);
    baseNoiseSamplingCoordinates += properties.height * float3(_WindDirection.x, _WindDirection.y, 0.0f) * _AltitudeDistortion;
    float lowFrequencyNoise = tex3Dlod(_Worley128RGBA, float4(baseNoiseSamplingCoordinates.xyz, noiseMipOffset)).r;

    // Simple preset: coverage constant (here from the weather), no rain cloud, one cloud type.
    float coverage = _Coverage;
    if (coverage <= CLOUD_DENSITY_TRESHOLD) return;
    float3 densityErosionAO = tex2Dlod(_CloudLutTexture, float4(0.5, properties.height, 0, 0)).rgb;

    float shapeFactor = lerp(0.1, 1.0, _ShapeFactor) * densityErosionAO.y;
    float erosionFactor = _ErosionFactor * densityErosionAO.y;

    lowFrequencyNoise = lerp(1.0, lowFrequencyNoise, shapeFactor);
    float base_cloud = 1.0 - densityErosionAO.x * coverage * (1.0 - shapeFactor);
    base_cloud = saturate(DensityRemap(lowFrequencyNoise, base_cloud, 1.0, 0.0, 1.0)) * coverage * coverage;

    properties.ambientOcclusion = densityErosionAO.z;
    properties.sigmaT = 0.04; // lerp(0.04, 0.12, rainClouds) with no rain clouds
    float ambientOcclusionBlend = saturate(1.0 - max(erosionFactor, shapeFactor) * 0.5);
    properties.ambientOcclusion = lerp(1.0, properties.ambientOcclusion, ambientOcclusionBlend);

    if (!cheapVersion)
    {
        float3 erosionCoords = AnimateErosionNoisePosition(positionPS) / NOISE_TEXTURE_NORMALIZATION_FACTOR * _ErosionScale;
        float erosionNoise = 1.0 - tex3Dlod(_ErosionNoise, float4(erosionCoords, erosionMipOffset)).r;
        erosionNoise = lerp(0.0, erosionNoise, erosionFactor * 0.75f * coverage * _ErosionFactorCompensation);
        properties.ambientOcclusion = saturate(properties.ambientOcclusion - sqrt(erosionNoise * _ErosionOcclusion));
        base_cloud = DensityRemap(base_cloud, erosionNoise, 1.0, 0.0, 1.0);
    }
    if (lightSampling) base_cloud -= erosionFactor * 0.1;
    base_cloud = max(0, base_cloud);
    properties.density = base_cloud * _DensityMultiplier;
}

struct VolumetricRayResult { float3 scattering; float ambient; float transmittance; float meanDistance; bool invalidRay; };

float3 EvaluateSunTransmittance(float3 positionPS, float3 sunDirection, float2 phaseFunction)
{
    float totalLightDistance = 0.0;
    float3 transmittance = 0.0f;
    if (ExitCloudVolume(positionPS, sunDirection, _HighestCloudAltitude, totalLightDistance))
    {
        totalLightDistance = clamp(totalLightDistance, 0, _NumLightSteps * LIGHT_STEP_MAXIMAL_SIZE);
        totalLightDistance += 5.0f;
        float intervalSize = totalLightDistance / _NumLightSteps;
        float opticalDepth = 0;
        for (int j = 0; j < (int)_NumLightSteps; j++)
        {
            float dist = intervalSize * (0.25 + j);
            float3 currentSamplePointPS = positionPS + sunDirection * dist;
            CloudProperties lightRayCloudProperties;
            EvaluateCloudProperties(currentSamplePointPS, 3.0f * j / _NumLightSteps, 0.0, true, true, lightRayCloudProperties);
            opticalDepth += lightRayCloudProperties.density * lightRayCloudProperties.sigmaT;
        }
        float3 extinction = intervalSize * opticalDepth * _ScatteringTint;
        for (int o = 0; o < NUM_MULTI_SCATTERING_OCTAVES; ++o)
        {
            float msFactor = CloudPositivePow(_MultiScattering, o);
            transmittance += exp(-extinction * msFactor) * (phaseFunction[o] * msFactor);
        }
    }
    return transmittance;
}

void EvaluateCloud(CloudProperties cloudProperties, EnvironmentLighting envLighting, float3 currentPositionPS, float stepSize, inout VolumetricRayResult volumetricRay)
{
    const float extinction = cloudProperties.density * cloudProperties.sigmaT;
    const float transmittance = exp(-extinction * stepSize);
    float powderEffect = PowderEffect(cloudProperties.density, envLighting.cosAngle, _PowderEffectIntensity);
    float3 sunTransmittance = EvaluateSunTransmittance(currentPositionPS, envLighting.sunDirection, envLighting.phaseFunction);
    float3 sunLuminance = sunTransmittance * powderEffect;
    float ambientLuminance = cloudProperties.ambientOcclusion;
    volumetricRay.scattering += sunLuminance * (volumetricRay.transmittance - volumetricRay.transmittance * transmittance);
    volumetricRay.ambient += ambientLuminance * (volumetricRay.transmittance - volumetricRay.transmittance * transmittance);
    volumetricRay.transmittance *= transmittance;
}

float DensityFadeValue(float distanceToCamera) { return saturate((distanceToCamera - _FadeInStart) / (_FadeInStart + _FadeInDistance)); }
float ErosionMipOffset(float distanceToCamera) { return lerp(0.0, 4.0, saturate((distanceToCamera - MIN_EROSION_DISTANCE) / (MAX_EROSION_DISTANCE - MIN_EROSION_DISTANCE))); }

VolumetricRayResult TraceVolumetricRay(CloudRay cloudRay)
{
    VolumetricRayResult volumetricRay;
    volumetricRay.scattering = 0.0; volumetricRay.ambient = 0.0; volumetricRay.transmittance = 1.0;
    volumetricRay.meanDistance = CLOUD_FLT_MAX; volumetricRay.invalidRay = true;

    float rangeStart, rangeEnd;
    if (IntersectCloudVolume(ConvertToPS(cloudRay.originWS), cloudRay.direction, _LowestCloudAltitude, _HighestCloudAltitude, rangeStart, rangeEnd))
    {
        if (cloudRay.maxRayLength >= rangeStart)
        {
            volumetricRay.meanDistance = 0.0;
            float totalDistance = min(rangeEnd, cloudRay.maxRayLength) - rangeStart;
            float stepS = min(totalDistance / _NumPrimarySteps, _MaxStepSize);
            totalDistance = stepS * _NumPrimarySteps;
            cloudRay.envLighting = EvaluateEnvironmentLighting(cloudRay);

            int currentIndex = 0;
            float meanDistanceDivider = 0.0f;
            float currentDistance = cloudRay.integrationNoise * stepS;
            float3 currentPositionWS = cloudRay.originWS + (rangeStart + currentDistance) * cloudRay.direction;
            bool activeSampling = true;
            int sequentialEmptySamples = 0;

            [loop]
            while (currentIndex < (int)_NumPrimarySteps && currentDistance < totalDistance)
            {
                float densityAttenuationValue = DensityFadeValue(rangeStart + currentDistance);
                float erosionMipOffset = ErosionMipOffset(rangeStart + currentDistance);
                float3 currentPositionPS = ConvertToPS(currentPositionWS);
                if (activeSampling)
                {
                    CloudProperties cloudProperties;
                    EvaluateCloudProperties(currentPositionPS, 0.0f, erosionMipOffset, false, false, cloudProperties);
                    cloudProperties.density *= densityAttenuationValue;
                    if (cloudProperties.density > CLOUD_DENSITY_TRESHOLD)
                    {
                        volumetricRay.meanDistance += (rangeStart + currentDistance) * volumetricRay.transmittance;
                        meanDistanceDivider += volumetricRay.transmittance;
                        EvaluateCloud(cloudProperties, cloudRay.envLighting, currentPositionPS, stepS, volumetricRay);
                        if (volumetricRay.transmittance < 0.003) { volumetricRay.transmittance = 0.0; break; }
                        sequentialEmptySamples = 0;
                    }
                    else sequentialEmptySamples++;
                    if (sequentialEmptySamples == EMPTY_STEPS_BEFORE_LARGE_STEPS) activeSampling = false;
                    currentPositionWS += cloudRay.direction * stepS;
                    currentDistance += stepS;
                }
                else
                {
                    CloudProperties cloudProperties;
                    EvaluateCloudProperties(currentPositionPS, 1.0f, 0.0, true, false, cloudProperties);
                    cloudProperties.density *= densityAttenuationValue;
                    if (cloudProperties.density < CLOUD_DENSITY_TRESHOLD)
                    {
                        currentPositionWS += cloudRay.direction * stepS * 2.0f;
                        currentDistance += stepS * 2.0f;
                    }
                    else
                    {
                        currentPositionWS -= cloudRay.direction * stepS;
                        currentDistance -= stepS;
                        currentIndex -= 1;
                        activeSampling = true;
                        sequentialEmptySamples = 0;
                    }
                }
                currentIndex++;
            }

            if (volumetricRay.meanDistance != 0.0)
            {
                volumetricRay.invalidRay = false;
                volumetricRay.meanDistance /= meanDistanceDivider;
                volumetricRay.meanDistance = min(volumetricRay.meanDistance, cloudRay.maxRayLength);
                float3 currentPositionPS = ConvertToPS(cloudRay.originWS) + volumetricRay.meanDistance * cloudRay.direction;
                float relativeHeight = EvaluateNormalizedCloudHeight(currentPositionPS);
                float3 ambient = max(0, lerp(_AmbientBottom, _AmbientTop, relativeHeight));
                volumetricRay.scattering = _SunLightColor * volumetricRay.scattering;
                volumetricRay.scattering += ambient * volumetricRay.ambient;
            }
        }
    }
    return volumetricRay;
}

#endif
