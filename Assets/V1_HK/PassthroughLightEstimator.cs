using System.Collections.Generic;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

using UnityPose = UnityEngine.Pose;

/// <summary>
/// Estimates room lighting from a passthrough camera and drives Unity's ambient
/// probe plus one directional "key" light.
///
/// The pipeline, per update:
///
///   1. Downsample the camera texture on the GPU to a tiny grid (box-filtered
///      via a halving chain, so every source pixel contributes).
///   2. Async readback of that grid. Nothing blocks the main thread.
///   3. Splat each texel into a persistent equal-area directional bin grid,
///      using the camera pose cached at request time. Bins the camera cannot
///      currently see keep their previous value, which is how a ~70 deg FOV
///      accumulates into something sphere-shaped as the user looks around.
///   4. Project the bins into an L2 spherical harmonic, convolve for Lambert,
///      write to <c>RenderSettings.ambientProbe</c>.
///   5. Take the brightest cone of bins as the key light direction and colour.
///
/// What this deliberately does NOT do: measure absolute radiance. The camera
/// auto-exposes and clips, so everything here is relative and scaled by hand
/// via <see cref="exposureScale"/>. Bright fixtures read as "somewhat bright",
/// not "1200 lux". Hand-placed lights are still what sells the shadows.
/// </summary>
[DisallowMultipleComponent]
public sealed class PassthroughLightEstimator : MonoBehaviour
{
    [Header("Source")]
    [SerializeField]
    private PassthroughCameraAccess cameraAccess;

    [Tooltip("Estimation updates per second. This is cheap; 4 is plenty, and slower means steadier.")]
    [SerializeField, Range(0.5f, 30f)]
    private float updateRate = 4f;

    [Header("Sampling")]
    [Tooltip("Downsampled camera grid. 16x12 is 192 rays per update, which is ample for L2.")]
    [SerializeField, Min(4)]
    private int gridWidth = 16;

    [SerializeField, Min(4)]
    private int gridHeight = 12;

    [Tooltip("Directional bins around the sphere. Equal-area, so every bin carries the same solid angle.")]
    [SerializeField, Min(4)]
    private int binColumns = 16;

    [SerializeField, Min(2)]
    private int binRows = 8;

    [Tooltip(
        "Blend weight applied to a bin each time the camera sees it. Small = " +
        "slow and stable, large = responsive but flickers as auto-exposure hunts."
    )]
    [SerializeField, Range(0.01f, 1f)]
    private float binBlend = 0.15f;

    [Tooltip(
        "Bins never observed are filled with this fraction of the mean observed " +
        "radiance. 1 = assume the unseen half of the room matches the seen half."
    )]
    [SerializeField, Range(0f, 2f)]
    private float unseenFill = 0.8f;

    [Header("Colour handling")]
    [Tooltip(
        "Apply sRGB -> linear on the CPU. Leave OFF in a Linear colour space " +
        "project (the GPU sampler already did it). Turn ON if the project is " +
        "Gamma, or if estimates look washed out and desaturated."
    )]
    [SerializeField]
    private bool decodeSrgbOnCpu;

    [Tooltip("Global multiplier on everything. The camera is not radiometrically calibrated, so this is your exposure dial.")]
    [SerializeField, Min(0f)]
    private float exposureScale = 1f;

    [Header("Ambient")]
    [SerializeField]
    private bool driveAmbientProbe = true;

    [SerializeField, Min(0f)]
    private float ambientIntensity = 1f;

    [Header("Key light")]
    [Tooltip("Optional. Directional light steered toward the brightest region of the room.")]
    [SerializeField]
    private Light keyLight;

    [Tooltip("Half-angle of the cone gathered around the brightest bin to form the key light.")]
    [SerializeField, Range(5f, 90f)]
    private float keyConeDeg = 35f;

    [Tooltip("Scales the key light's derived intensity.")]
    [SerializeField, Min(0f)]
    private float keyIntensityScale = 1f;

    [SerializeField, Min(0f)]
    private float keyIntensityCeiling = 3f;

    [Tooltip(
        "Fraction of the key cone's energy removed from the ambient probe, so " +
        "the dominant light is not counted twice. 0 = ambient keeps everything."
    )]
    [SerializeField, Range(0f, 1f)]
    private float keyAmbientSubtract = 0.6f;

    [Tooltip("Seconds to converge ~63% of a change in key direction, colour and intensity.")]
    [SerializeField, Min(0f)]
    private float keySmoothTime = 0.5f;

    [Header("Diagnostics")]
    [Tooltip("Vertical flip of the readback. If the key light tracks the floor when you point at the ceiling, toggle this.")]
    [SerializeField]
    private bool flipReadbackVertically;

    [SerializeField]
    private bool drawBinGizmos;

    [SerializeField]
    private bool logEstimates;

    // ---- state ------------------------------------------------------------

    private RenderTexture smallTarget;
    private Vector3[] localRays;          // camera-local ray per grid texel
    private Vector2Int cachedResolution;

    private Color[] binRadiance;
    private float[] binConfidence;
    private Color[] frameSum;
    private int[] frameCount;

    private bool readbackPending;
    private UnityPose requestPose;

    private float nextUpdateTime;

    private Vector3 keyDirection = Vector3.down;   // direction the light travels
    private Color keyColour = Color.white;
    private float keyIntensity;
    private bool hasKey;

    private readonly float[] basis = new float[9];
    private readonly float[] shR = new float[9];
    private readonly float[] shG = new float[9];
    private readonly float[] shB = new float[9];

    private static readonly float[] LambertConvolution =
    {
        1f,
        2f / 3f, 2f / 3f, 2f / 3f,
        0.25f, 0.25f, 0.25f, 0.25f, 0.25f
    };

    /// <summary>True once at least one readback has landed.</summary>
    public bool HasEstimate => hasKey;

    /// <summary>World-space direction the estimated key light travels.</summary>
    public Vector3 KeyDirection => keyDirection;

    public Color KeyColour => keyColour;

    public float KeyIntensity => keyIntensity;

    /// <summary>Mean observed radiance across all confident bins. Useful as a crude exposure readout.</summary>
    public Color MeanRadiance { get; private set; }

    // ---- lifecycle --------------------------------------------------------

    private void OnEnable()
    {
        AllocateBins();
        nextUpdateTime = 0f;
    }

    private void OnDisable()
    {
        ReleaseTarget();
        localRays = null;
        readbackPending = false;
    }

    private void AllocateBins()
    {
        int count = Mathf.Max(8, binColumns * binRows);

        binRadiance = new Color[count];
        binConfidence = new float[count];
        frameSum = new Color[count];
        frameCount = new int[count];
    }

    private void ReleaseTarget()
    {
        if (smallTarget == null)
            return;

        smallTarget.Release();
        DestroyImmediate(smallTarget);
        smallTarget = null;
    }

    // ---- capture ----------------------------------------------------------

    private void Update()
    {
        if (cameraAccess == null || !cameraAccess.IsPlaying)
            return;

        if (Time.unscaledTime < nextUpdateTime || readbackPending)
            return;

        Texture source = cameraAccess.GetTexture();

        if (source == null || source.width < 32 || source.height < 32)
            return;

        if (!EnsureRayCache())
            return;

        EnsureTarget();
        Downsample(source);

        // Cache the pose that goes with THIS frame before anything async starts.
        requestPose = cameraAccess.GetCameraPose();

        nextUpdateTime = Time.unscaledTime + 1f / Mathf.Max(0.5f, updateRate);

        if (SystemInfo.supportsAsyncGPUReadback)
        {
            readbackPending = true;

            AsyncGPUReadback.Request(
                smallTarget,
                0,
                TextureFormat.RGBAFloat,
                OnReadbackComplete
            );
        }
        else
        {
            ReadbackSynchronously();
        }
    }

    private void EnsureTarget()
    {
        if (smallTarget != null &&
            smallTarget.width == gridWidth &&
            smallTarget.height == gridHeight)
        {
            return;
        }

        ReleaseTarget();

        // Half-float so the linearised values survive without 8-bit banding in
        // the darks, which is most of a normally-lit room.
        smallTarget = new RenderTexture(
            gridWidth,
            gridHeight,
            0,
            GraphicsFormat.R16G16B16A16_SFloat
        )
        {
            name = "PassthroughLightEstimator/Small",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        smallTarget.Create();
    }

    /// <summary>
    /// Halve repeatedly, then land on the target size. A single Blit from a
    /// 1280x960 source to 16x12 would bilinear-sample four pixels per texel and
    /// call it an average; this actually averages the whole image.
    /// </summary>
    private void Downsample(Texture source)
    {
        int width = source.width;
        int height = source.height;

        RenderTexture current = null;
        Texture input = source;

        while (width > gridWidth * 2 && height > gridHeight * 2)
        {
            width = Mathf.Max(gridWidth, width / 2);
            height = Mathf.Max(gridHeight, height / 2);

            RenderTexture next = RenderTexture.GetTemporary(
                width,
                height,
                0,
                GraphicsFormat.R16G16B16A16_SFloat
            );

            next.filterMode = FilterMode.Bilinear;

            Graphics.Blit(input, next);

            if (current != null)
                RenderTexture.ReleaseTemporary(current);

            current = next;
            input = next;
        }

        Graphics.Blit(input, smallTarget);

        if (current != null)
            RenderTexture.ReleaseTemporary(current);
    }

    private void OnReadbackComplete(AsyncGPUReadbackRequest request)
    {
        readbackPending = false;

        if (request.hasError || !isActiveAndEnabled)
            return;

        NativeArray<Color> data = request.GetData<Color>();

        if (data.Length < gridWidth * gridHeight)
            return;

        Integrate(data);
    }

    private void ReadbackSynchronously()
    {
        var readable = new Texture2D(
            gridWidth,
            gridHeight,
            TextureFormat.RGBAFloat,
            false,
            true
        );

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = smallTarget;

        readable.ReadPixels(new Rect(0, 0, gridWidth, gridHeight), 0, 0, false);
        readable.Apply(false);

        RenderTexture.active = previous;

        NativeArray<Color> data = readable.GetRawTextureData<Color>();
        Integrate(data);

        Destroy(readable);
    }

    // ---- integration ------------------------------------------------------

    private void Integrate(NativeArray<Color> data)
    {
        if (localRays == null || binRadiance == null)
            return;

        int binCount = binColumns * binRows;

        if (binRadiance.Length != binCount)
            AllocateBins();

        for (int i = 0; i < binCount; i++)
        {
            frameSum[i] = Color.clear;
            frameCount[i] = 0;
        }

        Quaternion rotation = requestPose.rotation;

        for (int y = 0; y < gridHeight; y++)
        {
            int sourceRow = flipReadbackVertically ? gridHeight - 1 - y : y;

            for (int x = 0; x < gridWidth; x++)
            {
                int rayIndex = y * gridWidth + x;
                int dataIndex = sourceRow * gridWidth + x;

                Vector3 world = rotation * localRays[rayIndex];
                int bin = BinIndex(world);

                Color sample = data[dataIndex];

                if (decodeSrgbOnCpu)
                    sample = SrgbToLinear(sample);

                sample *= exposureScale;

                frameSum[bin] += sample;
                frameCount[bin]++;
            }
        }

        for (int i = 0; i < binCount; i++)
        {
            if (frameCount[i] == 0)
                continue;

            Color observed = frameSum[i] / frameCount[i];

            binRadiance[i] = binConfidence[i] > 0f
                ? Color.Lerp(binRadiance[i], observed, binBlend)
                : observed;

            binConfidence[i] = Mathf.Min(1f, binConfidence[i] + binBlend);
        }

        Solve();
    }

    /// <summary>
    /// Equal-area bin: rows are uniform in cos(elevation), so every bin covers
    /// 4*pi / (rows*cols) steradians and no solid-angle weighting is needed.
    /// </summary>
    private int BinIndex(Vector3 direction)
    {
        direction = direction.normalized;

        int row = Mathf.Clamp(
            Mathf.FloorToInt((1f - direction.y) * 0.5f * binRows),
            0,
            binRows - 1
        );

        float azimuth = Mathf.Atan2(direction.z, direction.x) / (2f * Mathf.PI) + 0.5f;

        int column = Mathf.Clamp(
            Mathf.FloorToInt(azimuth * binColumns),
            0,
            binColumns - 1
        );

        return row * binColumns + column;
    }

    /// <summary>Centre direction of a bin.</summary>
    private Vector3 BinDirection(int index)
    {
        int row = index / binColumns;
        int column = index % binColumns;

        float cosElevation = 1f - 2f * (row + 0.5f) / binRows;
        float sinElevation = Mathf.Sqrt(Mathf.Max(0f, 1f - cosElevation * cosElevation));
        float azimuth = ((column + 0.5f) / binColumns - 0.5f) * 2f * Mathf.PI;

        return new Vector3(
            sinElevation * Mathf.Cos(azimuth),
            cosElevation,
            sinElevation * Mathf.Sin(azimuth)
        );
    }

    // ---- solve ------------------------------------------------------------

    private void Solve()
    {
        int binCount = binColumns * binRows;

        // Mean of what we have actually seen, used to fill the unseen bins.
        Color seenSum = Color.clear;
        float seenWeight = 0f;

        for (int i = 0; i < binCount; i++)
        {
            if (binConfidence[i] <= 0f)
                continue;

            seenSum += binRadiance[i] * binConfidence[i];
            seenWeight += binConfidence[i];
        }

        if (seenWeight <= 0f)
            return;

        Color mean = seenSum / seenWeight;
        MeanRadiance = mean;

        Color fill = mean * unseenFill;

        // --- key light: brightest bin, then a cone gathered around it -------
        int brightest = -1;
        float brightestLuminance = float.NegativeInfinity;

        for (int i = 0; i < binCount; i++)
        {
            if (binConfidence[i] <= 0f)
                continue;

            float luminance = Luminance(binRadiance[i]);

            if (luminance > brightestLuminance)
            {
                brightestLuminance = luminance;
                brightest = i;
            }
        }

        Vector3 coneAxis = brightest >= 0 ? BinDirection(brightest) : Vector3.up;
        float cosCone = Mathf.Cos(keyConeDeg * Mathf.Deg2Rad);

        Vector3 keyCentroid = Vector3.zero;
        Color keySum = Color.clear;
        float keyWeight = 0f;
        float meanLuminance = Luminance(mean);

        for (int i = 0; i < binCount; i++)
        {
            if (binConfidence[i] <= 0f)
                continue;

            Vector3 direction = BinDirection(i);

            if (Vector3.Dot(direction, coneAxis) < cosCone)
                continue;

            // Only the excess over the room average counts as "the light".
            float excess = Mathf.Max(0f, Luminance(binRadiance[i]) - meanLuminance);

            if (excess <= 0f)
                continue;

            keyCentroid += direction * excess;
            keySum += binRadiance[i] * excess;
            keyWeight += excess;
        }

        Vector3 targetDirection;
        Color targetColour;
        float targetIntensity;

        if (keyWeight > 1e-5f && keyCentroid.sqrMagnitude > 1e-8f)
        {
            Vector3 toLight = keyCentroid.normalized;
            targetDirection = -toLight;                       // direction of travel

            Color average = keySum / keyWeight;
            float peak = Mathf.Max(1e-5f, Luminance(average));

            targetColour = new Color(
                average.r / peak,
                average.g / peak,
                average.b / peak,
                1f
            );

            targetIntensity = Mathf.Min(
                keyIntensityCeiling,
                keyWeight / (binColumns * binRows) * Mathf.PI * keyIntensityScale
            );
        }
        else
        {
            targetDirection = Vector3.down;
            targetColour = Color.white;
            targetIntensity = 0f;
        }

        float blend = hasKey
            ? 1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(1e-4f, keySmoothTime))
            : 1f;

        keyDirection = Vector3.Slerp(keyDirection, targetDirection, blend).normalized;
        keyColour = Color.Lerp(keyColour, targetColour, blend);
        keyIntensity = Mathf.Lerp(keyIntensity, targetIntensity, blend);
        hasKey = true;

        if (keyLight != null)
        {
            keyLight.transform.rotation = Quaternion.LookRotation(keyDirection);
            keyLight.color = keyColour;
            keyLight.intensity = keyIntensity;
        }

        // --- ambient probe ---------------------------------------------------
        if (driveAmbientProbe)
            BuildAmbientProbe(binCount, fill, coneAxis, cosCone);

        if (logEstimates)
        {
            Debug.Log(
                $"[LightEstimator] mean {mean} lum {meanLuminance:F4}, " +
                $"key dir {keyDirection} colour {keyColour} intensity {keyIntensity:F3}",
                this
            );
        }
    }

    private void BuildAmbientProbe(int binCount, Color fill, Vector3 coneAxis, float cosCone)
    {
        for (int i = 0; i < 9; i++)
        {
            shR[i] = 0f;
            shG[i] = 0f;
            shB[i] = 0f;
        }

        // Equal-area bins: identical solid angle each, summing to 4*pi.
        float solidAngle = 4f * Mathf.PI / binCount;

        for (int i = 0; i < binCount; i++)
        {
            Vector3 direction = BinDirection(i);

            Color radiance = binConfidence[i] > 0f
                ? Color.Lerp(fill, binRadiance[i], binConfidence[i])
                : fill;

            // Do not let the key light contribute twice.
            if (keyAmbientSubtract > 0f &&
                Vector3.Dot(direction, coneAxis) >= cosCone)
            {
                radiance *= 1f - keyAmbientSubtract;
            }

            EvaluateBasis(direction, basis);

            for (int c = 0; c < 9; c++)
            {
                float weighted = basis[c] * solidAngle;

                shR[c] += radiance.r * weighted;
                shG[c] += radiance.g * weighted;
                shB[c] += radiance.b * weighted;
            }
        }

        var probe = new SphericalHarmonicsL2();

        for (int c = 0; c < 9; c++)
        {
            float convolution = LambertConvolution[c] * ambientIntensity;

            probe[0, c] = shR[c] * convolution;
            probe[1, c] = shG[c] * convolution;
            probe[2, c] = shB[c] * convolution;
        }

        RenderSettings.ambientMode = AmbientMode.Custom;
        RenderSettings.ambientProbe = probe;
    }

    /// <summary>
    /// Real SH basis in Unity's coefficient order: DC, then y/z/x, then the five
    /// quadratics. Matches what ShadeSH9 expects, so the probe can be assigned
    /// straight to RenderSettings.
    /// </summary>
    private static void EvaluateBasis(Vector3 d, float[] y)
    {
        y[0] = 0.2820948f;
        y[1] = 0.4886025f * d.y;
        y[2] = 0.4886025f * d.z;
        y[3] = 0.4886025f * d.x;
        y[4] = 1.0925484f * d.x * d.y;
        y[5] = 1.0925484f * d.y * d.z;
        y[6] = 0.3153916f * (3f * d.z * d.z - 1f);
        y[7] = 1.0925484f * d.x * d.z;
        y[8] = 0.5462742f * (d.x * d.x - d.y * d.y);
    }

    private static float Luminance(Color c) =>
        0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

    private static Color SrgbToLinear(Color c) => new Color(
        Mathf.GammaToLinearSpace(c.r),
        Mathf.GammaToLinearSpace(c.g),
        Mathf.GammaToLinearSpace(c.b),
        1f
    );

    // ---- ray cache --------------------------------------------------------

    /// <summary>
    /// Camera-local direction for each grid texel, built once per resolution.
    ///
    /// Rather than re-derive the unprojection from intrinsics (and get the
    /// principal-point offset subtly wrong), this asks the SDK for a world ray
    /// per texel and immediately strips the current camera rotation back out.
    /// Whatever distortion model Meta applies, we inherit it.
    /// </summary>
    private bool EnsureRayCache()
    {
        Vector2 resolution = cameraAccess.CurrentResolution;

        var current = new Vector2Int(
            Mathf.RoundToInt(resolution.x),
            Mathf.RoundToInt(resolution.y)
        );

        if (localRays != null &&
            localRays.Length == gridWidth * gridHeight &&
            current == cachedResolution)
        {
            return true;
        }

        if (current.x <= 0 || current.y <= 0)
            return false;

        UnityPose pose = cameraAccess.GetCameraPose();
        Quaternion inverse = Quaternion.Inverse(pose.rotation);

        localRays = new Vector3[gridWidth * gridHeight];

        for (int y = 0; y < gridHeight; y++)
        {
            float v = (y + 0.5f) / gridHeight;

            for (int x = 0; x < gridWidth; x++)
            {
                float u = (x + 0.5f) / gridWidth;

                Ray ray = cameraAccess.ViewportPointToRay(new Vector2(u, v));

                localRays[y * gridWidth + x] =
                    (inverse * ray.direction).normalized;
            }
        }

        cachedResolution = current;
        return true;
    }

    // ---- diagnostics ------------------------------------------------------

    private void OnDrawGizmosSelected()
    {
        if (!drawBinGizmos || binRadiance == null)
            return;

        Vector3 origin = transform.position;
        int binCount = binColumns * binRows;

        for (int i = 0; i < binCount && i < binRadiance.Length; i++)
        {
            if (binConfidence[i] <= 0f)
                continue;

            Vector3 direction = BinDirection(i);

            Gizmos.color = binRadiance[i];
            Gizmos.DrawRay(origin, direction * (0.25f + Luminance(binRadiance[i])));
        }

        if (!hasKey)
            return;

        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(origin, -keyDirection * 1.5f);
    }
}
