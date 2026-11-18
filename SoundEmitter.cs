using System;
using System.Collections.Generic;
using UnityEngine;
using static SoundWorld;

public class SoundEmitter : MonoBehaviour
{
    [Header("Sound identity")]
    [Tooltip("Categoria do som, usada para legendas/glyphs e filtragem por gameplay.")]
    public SoundCategory category;

    [Tooltip("Point = fonte pontual com posição própria; Global = som ambiente sem posição fixa (usa aberturas/openness).")]
    [SerializeField] private SoundSourceType sourceType = SoundSourceType.Point;

    [Header("Sound Caption")]
    [Tooltip("Legendas associadas a cada AudioClip que este emissor pode tocar.")]
    [SerializeField] private CaptionedClip[] captionedClips;

    [Tooltip("Volume base do emissor (0-1), antes de qualquer atenuação por distância/obstrução.")]
    [Range(0f, 10f)]
    public float loudness = 1f;

    [Header("Smoothing")]
    [Tooltip("Velocidade de suavização (lerp) do volume percebido em direção ao valor alvo.")]
    [SerializeField] private float volumeLerpSpeed = 10f;

    [Tooltip("Velocidade de suavização (lerp) do cutoff do low-pass filter em direção ao valor alvo.")]
    [SerializeField] private float cutoffLerpSpeed = 8f;

    [Tooltip("Cutoff mínimo (Hz) do low-pass filter quando o som está totalmente obstruído.")]
    [SerializeField] private float minimumOccludedCutoff = 600f;

    [Header("Proxy AudioSource")]
    [SerializeField] private bool PlayOnAwake = false;

    [Tooltip("AudioSource usado para sons pontuais (posicionados no espaço 3D).")]
    [SerializeField] private AudioSource pointAudioSource;

    [Tooltip("AudioSource usado para sons globais/ambiente (mono ou parcialmente espacializado, sem posição fixa).")]
    [SerializeField]
    private AudioSource globalAudioSource;

    private AudioLowPassFilter globalLowPass;

    private float currentGlobalVolume;
    private float targetGlobalVolume;

    private float currentGlobalCutoff = 22000f;
    private float targetGlobalCutoff = 22000f;

    private Vector3 currentGlobalPosition;
    private Vector3 targetGlobalPositionRaw;

    [Tooltip("Quanto o volume da direção alinhada com uma abertura (janela/porta) é reforçado, no máximo (0 = sem reforço, 1 = dobra o volume).")]
    [Range(0f, 2f)]
    [SerializeField] private float openingClarityBoost = 2f;

    [Tooltip("Quão exigente é o alinhamento entre a direção de um entry e a abertura para receber o reforço de clareza. Valores maiores = feixe mais estreito.")]
    [SerializeField] private float openingAlignmentSharpness = 3f;

    [Tooltip("Fração de Openness (0-1, quanto do ambiente como um todo está aberto) necessária para o volume/muffle 'base' (sem considerar alinhamento com abertura específica) atingir o valor máximo. Valores baixos fazem frestas pequenas já soarem quase tão claras quanto um ambiente bem aberto; valores altos exigem mais abertura geral.")]
    [Range(0.01f, 1f)]
    [SerializeField] private float openSpaceVolumeSensitivity = 0.3f;

    private float currentGlobalSpatialBlend = 0f;
    private float targetGlobalSpatialBlend = 0f;

    [Header("Accessibility UI")]
    [Tooltip("Mostra o indicador visual genérico deste emissor na UI de acessibilidade.")]
    public bool showIndicator = true;

    [Tooltip("Mostra uma seta apontando para a direção percebida do som (desligado automaticamente para sons globais sem abertura alinhada).")]
    public bool showCaptionArrow = true;

    [Tooltip("Permite que o GlyphRenderer desenhe o glyph direcional deste emissor.")]
    public bool showGlyphs = true;

    private AudioLowPassFilter lowPassFilter;

    internal bool hadPerceptionThisFrame;

    private float targetVolume;
    private float targetCutoff = 22000f;

    private float lastValidCutoff = 22000f;

    public float lastCaptionTime;

    [Tooltip("Tempo mínimo (segundos) entre legendas repetidas deste emissor.")]
    public float captionCooldown = 1.5f;

    private float currentVolume;

    private Vector3 currentPointPosition;
    private Vector3 targetPointPositionRaw;
    public Vector3 PerceivedPosition => pointAudioSource.transform.position;

    private readonly Dictionary<AudioClip, CaptionedClip> captionLookup =
        new();

    private AudioClip currentClip;

    public bool CaptionShown { get; set; }

    public bool NeedsCatchUp { get; private set; }

    private float nextCaptionTime;

    public AudioClip CurrentClip => currentClip;
    public SoundSourceType SourceType => sourceType;

    public Vector3 VirtualPosition { get; private set; }

    public enum CaptionMode
    {
        None,
        Once,
        Repeat
    }

    public bool IsPlaying => sourceType == SoundSourceType.Point ? pointAudioSource != null && pointAudioSource.isPlaying
    : globalAudioSource != null && globalAudioSource.isPlaying;

    public Transform transformReference;

    private struct SourceDefaults
    {
        public float volume;
        public float dopplerLevel;
        public float minDistance;
        public float maxDistance;
        public AudioRolloffMode rolloffMode;
        public AnimationCurve customRolloff;
    }

    private SourceDefaults pointDefaults;
    private SourceDefaults globalDefaults;
    private bool usingStandardAudio;

    private static bool RayTracingEnabled =>
        GameSettings.Instance == null || GameSettings.Instance.UseRayTracing;

    private static SourceDefaults CaptureDefaults(AudioSource source)
    {
        if (source == null)
            return default;

        return new SourceDefaults
        {
            volume = source.volume,
            dopplerLevel = source.dopplerLevel,
            minDistance = source.minDistance,
            maxDistance = source.maxDistance,
            rolloffMode = source.rolloffMode,
            customRolloff = source.rolloffMode == AudioRolloffMode.Custom
                ? source.GetCustomCurve(AudioSourceCurveType.CustomRolloff)
                : null
        };
    }

    private void SetStandardAudioMode(bool enable)
    {
        usingStandardAudio = enable;

        ConfigureSourceForMode(pointAudioSource, lowPassFilter, pointDefaults, enable);
        ConfigureSourceForMode(globalAudioSource, globalLowPass, globalDefaults, enable);

        if (enable)
        {
            if (sourceType == SoundSourceType.Global)
                showCaptionArrow = false;
            return;
        }

        currentPointPosition = transform.position;
        targetPointPositionRaw = transform.position;
        currentVolume = 0f;
        targetVolume = 0f;
        currentGlobalPosition = transform.position;
        targetGlobalPositionRaw = transform.position;
        currentGlobalVolume = 0f;
        targetGlobalVolume = 0f;
        currentGlobalCutoff = 22000f;
        targetGlobalCutoff = 22000f;
        targetCutoff = 22000f;
        lastValidCutoff = 22000f;
    }

    private void ConfigureSourceForMode(AudioSource source, AudioLowPassFilter filter, SourceDefaults d, bool standard)
    {
        if (source == null)
            return;

        if (!standard)
        {
            ConfigureAudioSource(source, filter);

            if (filter != null)
                filter.enabled = true;

            return;
        }

        source.spatialBlend = (sourceType == SoundSourceType.Global) ? 0f : 1f;
        source.rolloffMode = d.rolloffMode;

        if (d.rolloffMode == AudioRolloffMode.Custom && d.customRolloff != null)
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, d.customRolloff);

        source.minDistance = d.minDistance;
        source.maxDistance = d.maxDistance;
        source.dopplerLevel = d.dopplerLevel;
        source.volume = d.volume;

        if (filter != null)
        {
            filter.cutoffFrequency = 22000f;
            filter.enabled = false;
        }
    }

    private void UpdateStandardAudio()
    {
        if (pointAudioSource != null && sourceType == SoundSourceType.Point)
            pointAudioSource.transform.position = transform.position;
    }

    /// <summary>
    /// Intensidade aproximada que a engine entregaria ao ouvinte (rolloff do
    /// AudioSource, sem oclusão). Usada pelo SoundListener só para decidir
    /// quando mostrar legendas/indicadores no modo sem ray tracing.
    /// Mesma escala do modo RT: loudness * atenuação.
    /// </summary>
    internal float EvaluateStandardIntensity(Vector3 listenerPosition)
    {
        if (sourceType == SoundSourceType.Global)
            return loudness;

        if (pointAudioSource == null)
            return 0f;

        float d = Vector3.Distance(transform.position, listenerPosition);
        float min = Mathf.Max(0.0001f, pointAudioSource.minDistance);
        float max = Mathf.Max(min + 0.0001f, pointAudioSource.maxDistance);

        float gain;

        switch (pointAudioSource.rolloffMode)
        {
            case AudioRolloffMode.Linear:
                gain = 1f - Mathf.Clamp01((d - min) / (max - min));
                break;

            case AudioRolloffMode.Custom:
                AnimationCurve curve = pointAudioSource.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                gain = curve != null ? Mathf.Clamp01(curve.Evaluate(Mathf.Clamp01(d / max))) : 1f;
                break;

            default: // Logarithmic
                gain = min / Mathf.Clamp(d, min, max);
                break;
        }

        return loudness * gain;
    }

    private void Awake()
    {
        currentPointPosition = transform.position;
        targetPointPositionRaw = transform.position;
        transformReference = transform;
        currentGlobalPosition = transform.position;
        targetGlobalPositionRaw = transform.position;
        targetGlobalCutoff = 22000f;
        currentGlobalCutoff = 22000f;
        currentGlobalSpatialBlend = 0f;
        targetGlobalSpatialBlend = 0f;

        if (pointAudioSource != null)
        {
            lowPassFilter = pointAudioSource.GetComponent<AudioLowPassFilter>();
        }

        if (globalAudioSource != null)
        {
            globalLowPass = globalAudioSource.GetComponent<AudioLowPassFilter>();
        }

        foreach (var data in captionedClips)
        {
            if (data == null || data.clip == null)
                continue;

            captionLookup[data.clip] = data;
        }

        pointDefaults = CaptureDefaults(pointAudioSource);
        globalDefaults = CaptureDefaults(globalAudioSource);

        if (pointAudioSource != null)
        {
            ConfigureAudioSource(pointAudioSource, lowPassFilter);
        }

        if (globalAudioSource != null)
        {
            ConfigureAudioSource(globalAudioSource, globalLowPass);
        }

        if (PlayOnAwake == true)
            PlayLoop(this.pointAudioSource.clip);

#if UNITY_EDITOR
        if (pointAudioSource != null && lowPassFilter == null)
            Debug.LogError($"{name}: pointAudioSource sem AudioLowPassFilter", this);
        if (globalAudioSource != null && globalLowPass == null)
            Debug.LogError($"{name}: globalAudioSource sem AudioLowPassFilter", this);
#endif
    }

    private void OnDisable()
    {
        SoundWorld.Instance?.UnregisterEmitter(this);
    }

    private void Update()
    {
        hadPerceptionThisFrame = false;

        bool wantStandard = !RayTracingEnabled;

        if (wantStandard != usingStandardAudio)
            SetStandardAudioMode(wantStandard);

        if (usingStandardAudio)
        {
            UpdateStandardAudio();
            return;
        }

        // Som pontual
        if (pointAudioSource != null)
        {
            if (lowPassFilter != null)
            {
                lowPassFilter.cutoffFrequency = Mathf.Lerp(lowPassFilter.cutoffFrequency, targetCutoff, Time.deltaTime * cutoffLerpSpeed);

                currentVolume = Mathf.Lerp(currentVolume, targetVolume, Time.deltaTime * volumeLerpSpeed);
            }

            if (currentVolume < 0.0005f && targetVolume < 0.0005f)
            {
                pointAudioSource.volume = 0f;
                return;
            }

            pointAudioSource.volume = currentVolume;

            currentPointPosition = Vector3.Lerp(currentPointPosition, targetPointPositionRaw, Time.deltaTime * cutoffLerpSpeed);

            pointAudioSource.transform.position = currentPointPosition;
        }

        // Som global
        if (globalAudioSource != null)
        {
            if (globalLowPass != null)
            {
                currentGlobalCutoff = Mathf.Lerp(currentGlobalCutoff, targetGlobalCutoff, Time.deltaTime * cutoffLerpSpeed);

                globalLowPass.cutoffFrequency = currentGlobalCutoff;
            }

            currentGlobalVolume = Mathf.Lerp(currentGlobalVolume, targetGlobalVolume, Time.deltaTime * volumeLerpSpeed);

            if (currentGlobalVolume < 0.0005f && targetGlobalVolume < 0.0005)
            {
                globalAudioSource.volume = 0f;
                return;
            }

            globalAudioSource.volume = currentGlobalVolume;

            currentGlobalSpatialBlend = targetGlobalSpatialBlend;

            globalAudioSource.spatialBlend = currentGlobalSpatialBlend;

            const float spatialBlendEpsilon = 0.001f;

            if (currentGlobalSpatialBlend > spatialBlendEpsilon)
            {
                currentGlobalPosition = Vector3.Lerp(currentGlobalPosition, targetGlobalPositionRaw, Time.deltaTime * cutoffLerpSpeed);
                globalAudioSource.transform.position = currentGlobalPosition;
            }
        }
    }

    public void PlayOneShot(AudioClip clip)
    {
        if (clip == null)
            return;

        NeedsCatchUp = true;

        currentClip = clip;
        CaptionShown = false;

        if (sourceType == SoundSourceType.Point)
        {
            pointAudioSource.PlayOneShot(clip);
        }
        else
        {
            globalAudioSource.PlayOneShot(clip);
        }
    }

    public void PlayLoop(AudioClip clip)
    {
        if (clip == null)
            return;

        currentClip = clip;
        CaptionShown = false;

        if (sourceType == SoundSourceType.Point)
        {
            if (pointAudioSource.clip == clip && pointAudioSource.isPlaying)
                return;

            pointAudioSource.clip = clip;
            pointAudioSource.loop = true;
            pointAudioSource.Play();
        }
        else
        {
            if (globalAudioSource == null)
                return;

            globalAudioSource.clip = clip;
            globalAudioSource.loop = true;

            if (!globalAudioSource.isPlaying)
                globalAudioSource.Play();
        }
    }

    public void StopLoop()
    {
        currentClip = null;
        CaptionShown = false;

        if (sourceType == SoundSourceType.Point)
        {
            pointAudioSource.Stop();
        }
        else
        {
            globalAudioSource?.Stop();
        }
    }

    internal void ApplyPerception(float intensity, int wallHits, int maxBounces, float transmissionMuffle, Vector3 perceivedPoint)
    {
        hadPerceptionThisFrame = true;

        intensity = Mathf.Clamp01(intensity);

        float muffleCut = Mathf.Lerp(Mathf.Log(22000f), Mathf.Log(600f), transmissionMuffle);

        targetCutoff = Mathf.Max(minimumOccludedCutoff, Mathf.Exp(muffleCut));

        lastValidCutoff = targetCutoff;

        if (pointAudioSource != null)
        {
            targetPointPositionRaw = wallHits == 0 ? transform.position : perceivedPoint;

            targetVolume = intensity;
        }
    }

    internal void Silence()
    {
        targetVolume = 0f;
        targetCutoff = lastValidCutoff;
    }

    public void ResetCaption()
    {
        CaptionShown = false;
    }

    public bool CanShowRepeatedCaption(float interval)
    {
        if (Time.time < nextCaptionTime)
            return false;

        nextCaptionTime = Time.time + interval;
        return true;
    }

    public bool TryGetCaptionData(AudioClip clip, out CaptionedClip data)
    {
        if (clip == null)
        {
            data = null;
            return false;
        }

        return captionLookup.TryGetValue(
            clip,
            out data);
    }

    private void ConfigureAudioSource(AudioSource source, AudioLowPassFilter filter)
    {
        if (source == null)
            return;

        source.spatialBlend = (sourceType == SoundSourceType.Global) ? 0f : 1f;

        source.rolloffMode = AudioRolloffMode.Custom;

        source.SetCustomCurve(
            AudioSourceCurveType.CustomRolloff,
            new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(1f, 1f)));

        source.minDistance = 100000f;
        source.maxDistance = 100001f;

        source.dopplerLevel = 0f;
        source.playOnAwake = false;
        source.loop = true;
        source.volume = 0f;

        if (filter != null)
            filter.cutoffFrequency = 22000f;
    }

    internal void ApplyGlobalPerception(
    List<SoundWorld.GlobalEntry> entries,
    int maxBounces,
    bool isOpenSpace = false,
    Vector3 openingDirection = default,
    float openingStrength = 0f)
    {
        showCaptionArrow = false;
        hadPerceptionThisFrame = true;

        targetGlobalVolume = 0f;

        float openness = Mathf.Clamp01(SoundWorld.Instance.Openness);

        float spatialBlendTarget = Mathf.InverseLerp(
            0.0f,
            0.3f,
            Mathf.Pow((SoundWorld.Instance.Openness), 0.5f)
        );

        targetGlobalSpatialBlend = spatialBlendTarget;

        targetGlobalCutoff = 22000f;

        Vector3 listenerPos = SoundListener.Instance != null ?
        SoundListener.Instance.transform.position : transform.position;

        if (globalAudioSource != null)
            targetGlobalPositionRaw = listenerPos;

        if (entries == null || entries.Count == 0)
            return;

        // Espaço totalmente aberto -> um único som mono ao redor do jogador
        if (isOpenSpace)
        {
            targetGlobalVolume = Mathf.Clamp01(1f);
            targetGlobalCutoff = 22000f;

            if (globalAudioSource != null)
                targetGlobalPositionRaw = listenerPos + new Vector3(0f, 2f, 0f);

            return;
        }

        // Ambiente fechado (com ou sem uma abertura clara)

        float opennessFactor = Mathf.Clamp01(SoundWorld.Instance.Openness / Mathf.Max(0.001f, openSpaceVolumeSensitivity));

        bool hasOpening = openingStrength > 0f && openingDirection != Vector3.zero;
        Vector3 openingDirNormalized = hasOpening ? openingDirection.normalized : Vector3.zero;

        float strongestVolume = 0f;
        float closedMuffleWeightedSum = 0f;

        Vector3 weightedPoint = Vector3.zero;
        float weightSum = 0f;

        foreach (var entry in entries)
        {
            float volume = Mathf.Clamp01(entry.intensity);
            float muffle = entry.muffle;

            volume *= Mathf.Lerp(0.2f, 1f, opennessFactor);
            muffle = Mathf.Lerp(muffle, 0f, opennessFactor);

            if (hasOpening && entry.direction.sqrMagnitude > 0.0001f)
            {
                showCaptionArrow = true;
                float alignment = Mathf.Clamp01(Vector3.Dot(entry.direction.normalized, openingDirNormalized));
                float weight = Mathf.Pow(alignment, openingAlignmentSharpness) * openingStrength;

                muffle = Mathf.Lerp(muffle, 0f, weight);
                volume = Mathf.Lerp(volume, volume * openingClarityBoost, weight);
            }

            float scaledVolume = Mathf.Lerp(0.2f, 1f, volume);

            strongestVolume = Mathf.Max(strongestVolume, scaledVolume);
            closedMuffleWeightedSum += muffle * volume;
            weightedPoint += entry.point * volume;
            weightSum += volume;
        }

        targetGlobalVolume = Mathf.Clamp01(strongestVolume);

        float averageMuffle = weightSum > Mathf.Epsilon ? closedMuffleWeightedSum / weightSum : 0f;

        if (globalAudioSource != null)
        {
            if (hasOpening)
            {
                float proxyDistance = 5f;

                targetGlobalPositionRaw = listenerPos + openingDirNormalized * proxyDistance;

                VirtualPosition = targetGlobalPositionRaw;

                if (Fairy.Instance)
                {
                    if (Fairy.Instance.IsPlayerClose(Fairy.Instance.firstOpeningPoint))
                    {
                        Fairy.Instance.ReturnToStart();
                    }
                    else
                    {
                        Fairy.Instance.SetOpening(targetGlobalPositionRaw);
                    }
                }
            }
            else if (hasOpening && isOpenSpace)
            {
                Fairy.Instance.ReturnToStart();
            }
            else
            {
                Vector3 position = weightSum > Mathf.Epsilon
                    ? weightedPoint / weightSum
                    : listenerPos;

                targetGlobalPositionRaw = position;
                VirtualPosition = position;

                Fairy.Instance.ReturnToStart();
            }
        }

        float cutoffCurveSharpness = 17f;

        float opennessCutoffHz = 600f + (22000f - 600f) * (1f - Mathf.Exp(-cutoffCurveSharpness * SoundWorld.Instance.Openness)) / (1f - Mathf.Exp(-cutoffCurveSharpness));

        float muffleCutoffHz = Mathf.Exp(Mathf.Lerp(
            Mathf.Log(22000f),
            Mathf.Log(600f),
            Mathf.Clamp01(averageMuffle)));

        float cutoffHz = Mathf.Min(opennessCutoffHz, muffleCutoffHz);

        targetGlobalCutoff = Mathf.Clamp(cutoffHz, minimumOccludedCutoff, 22000f);
    }

    public void MarkCaughtUp()
    {
        NeedsCatchUp = false;
    }
}