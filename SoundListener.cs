using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

public class SoundListener : MonoBehaviour
{
    static public SoundListener Instance;

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector
    // ─────────────────────────────────────────────────────────────────────────

    [Tooltip("Número máximo de ricochetes por raio.")]
    [SerializeField] private int maxBounces = 3;

    [Tooltip("Taxa de atualização do RTA em Hz. 20 Hz é suficiente para jogos (50 ms de latência).")]
    [SerializeField] private float updateRate = 20f;

    [Header("Attenuation")]
    [Tooltip("Intensidade mínima para um som disparar legenda; abaixo disso é ignorado.")]
    [SerializeField] private float minAudibleIntensity = 0.02f;

    [Header("Glyphs")]
    [Tooltip("Componente responsável por desenhar os glyphs direcionais de acessibilidade.")]
    [SerializeField] private GlyphRenderer glyphRenderer;

    [Header("Events")]
    [Tooltip("Disparado a cada sinal de som processado neste tick, já suavizado.")]
    public UnityEvent<SoundSignal> OnBestSignalReceived;

    [Header("Temporal Persistence")]
    [Tooltip("Por quanto tempo (segundos) um som continua sendo percebido, com intensidade decaindo, depois de deixar de ser detectado.")]
    [SerializeField] private float persistenceTime = 0.35f;

    [Tooltip("Fator de decaimento aplicado à intensidade persistida a cada tick, além do decaimento linear pelo tempo restante.")]
    [SerializeField] private float persistenceIntensityDecay = 0.92f;

    [Header("Temporal Smoothing")]
    [Tooltip("Velocidade de suavização (lerp/slerp) de posição, direção, intensidade e distância percebidas.")]
    [SerializeField] private float smoothingSpeed = 10f;

    [Tooltip("Distância mínima entre ouvinte e ponto percebido para a direção ser suavizada por Slerp; abaixo disso, o valor bruto é usado direto (evita instabilidade quando a fonte está muito perto).")]
    [SerializeField] private float minDirectionDistance = 0.5f;


    // ─────────────────────────────────────────────────────────────────────────
    // Estado interno
    // ─────────────────────────────────────────────────────────────────────────

    private struct PersistedPath
    {
        public Vector3 perceivedPoint;
        public Vector3 perceivedDirection;
        public float intensity;
        public float distance;
        public float transmissionMuffle;
        public int wallHits;
        public Collider transmissionCollider;
        public bool hasReflectionPoint;

        public float lastSeenTime;

        public Vector3 smoothedPoint;
        public Vector3 smoothedDirection;
        public float smoothedIntensity;
        public float smoothedDistance;
    }

    private readonly Dictionary<EntityId, PersistedPath> persistedPaths = new();

    private readonly Dictionary<EntityId, SoundSignal> signals = new();

    public bool TryGetSignal(EntityId id, out SoundSignal signal)
    {
        return signals.TryGetValue(id, out signal);
    }

    private readonly HashSet<EntityId> updatedIds = new();

    // Controle temporal
    private float _updateAccum;
    private float _updateInterval;

    // Tempo real (Time.time) do último DispatchResults — usado para calcular
    // o deltaTime real da suavização, já que DispatchResults só roda a cada
    // _updateInterval (~50ms a 20Hz), não a cada frame renderizado.
    private float _lastDispatchTime;

    // Último modo conhecido da flag GameSettings.UseRayTracing (detecta troca em runtime).
    private bool _rayTracingActive = true;

    // ─────────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
        }
        else
        {
            Instance = this;
        }

        _updateInterval = 1f / Mathf.Max(1f, updateRate);
        _lastDispatchTime = Time.time;
    }

    private void Update()
    {
        _updateAccum += Time.deltaTime;
        if (_updateAccum < _updateInterval)
            return;

        _updateAccum -= _updateInterval;

        float elapsed = Time.time - _lastDispatchTime;
        _lastDispatchTime = Time.time;

        bool rayTracing = GameSettings.Instance == null || GameSettings.Instance.UseRayTracing;

        if (rayTracing != _rayTracingActive)
            OnRayTracingModeChanged(rayTracing);

        if (rayTracing)
        {
            // C2 — percepção por ray tracing.
            DispatchResults(
                SoundWorld.Instance.Process(transform.position),
                elapsed
            );
        }
        else
        {
            // C0/C1 — sem ray tracing: o áudio fica por conta da engine; aqui só
            // calculamos o que as legendas/indicadores precisam (distância/direção
            // diretas). O reverb do listener segue a flag UseListenerReverb.
            SoundWorld.Instance.ProcessReverbOnly(transform.position);
            DispatchStandard();
        }
    }

    private void OnRayTracingModeChanged(bool enabled)
    {
        _rayTracingActive = enabled;

        // Estados acumulados de um modo não valem para o outro.
        persistedPaths.Clear();
        signals.Clear();
        updatedIds.Clear();

        // A Fairy é guiada pelas aberturas detectadas via ray tracing.
        if (!enabled && Fairy.Instance != null)
            Fairy.Instance.ReturnToStart();
    }

    private void DispatchStandard()
    {
        Vector3 listenerPos = transform.position;

        foreach (SoundEmitter em in SoundWorld.Instance.Emitters)
        {
            if (em == null)
                continue;

            em.hadPerceptionThisFrame = false;

            EntityId id = em.GetEntityId();

            if (!em.IsPlaying)
            {
                signals.Remove(id);
                continue;
            }

            float intensity = em.EvaluateStandardIntensity(listenerPos);

            if (intensity <= minAudibleIntensity)
            {
                signals.Remove(id);
                continue;
            }

            Vector3 toEmitter = em.transform.position - listenerPos;
            float distance = toEmitter.magnitude;
            Vector3 direction = distance > 0.0001f ? toEmitter / distance : transform.forward;

            SoundSignal signal = new SoundSignal(
                em.transform.position,
                direction,
                intensity,
                em.loudness,
                0,
                distance,
                em.category,
                id,
                em,
                0f);

            signals[id] = signal;

            EmitSignal(em, signal, intensity);
        }
    }

    private void DispatchResults(List<SoundWorld.SoundResult> results, float elapsed)
    {
        updatedIds.Clear();

        foreach (SoundEmitter em in SoundWorld.Instance.Emitters)
            if (em != null)
                em.hadPerceptionThisFrame = false;

        float now = Time.time;

        foreach (var result in results)
        {
            updatedIds.Add(result.id);

            SoundEmitter em = result.emitter;
            if (em == null)
                continue;

            Vector3 perceivedDirection = result.perceivedDirection;
            Vector3 perceivedPoint = result.perceivedPoint;

            float intensity = result.intensity;
            float muffle = result.transmissionMuffle;
            int wallHits = result.wallHits;
            float distance = result.distance;

            if (SoundWorld.Instance != null && SoundWorld.Instance.DebugSettings.showPerceivedDirectionLines)
            {
                Debug.DrawLine(
                    transform.position,
                    perceivedPoint,
                    Color.yellow,
                    5f);
            }

            if (!persistedPaths.TryGetValue(result.id, out PersistedPath persisted))
            {
                persisted = new PersistedPath
                {
                    smoothedPoint = perceivedPoint,
                    smoothedDirection = perceivedDirection,
                    smoothedIntensity = intensity,
                    smoothedDistance = distance,
                };
            }

            float t = 1f - Mathf.Exp(-smoothingSpeed * elapsed);

            persisted.smoothedPoint =
                Vector3.Lerp(persisted.smoothedPoint, perceivedPoint, t);

            persisted.smoothedIntensity =
                Mathf.Lerp(persisted.smoothedIntensity, intensity, t);

            persisted.smoothedDistance =
                Mathf.Lerp(persisted.smoothedDistance, distance, t);

            if (em.SourceType == SoundWorld.SoundSourceType.Global)
            {
                persisted.smoothedDirection = perceivedDirection;
            }
            else if (Vector3.Distance(transform.position, persisted.smoothedPoint) > minDirectionDistance)
            {
                persisted.smoothedDirection = Vector3.Slerp(
                    persisted.smoothedDirection,
                    perceivedDirection,
                    t);
            }

            persisted.perceivedPoint = perceivedPoint;
            persisted.perceivedDirection = perceivedDirection;
            persisted.intensity = intensity;
            persisted.distance = distance;
            persisted.transmissionMuffle = muffle;
            persisted.wallHits = wallHits;
            persisted.transmissionCollider = result.transmissionCollider;
            persisted.hasReflectionPoint = result.hasReflectionPoint;
            persisted.lastSeenTime = now;

            persistedPaths[result.id] = persisted;

            if (em.SourceType == SoundWorld.SoundSourceType.Global)
            {
                em.ApplyGlobalPerception(
                    result.globalEntries,
                    maxBounces,
                    SoundWorld.Instance.IsOpenSpace,
                    SoundWorld.Instance.OpeningDirection,
                    SoundWorld.Instance.HasOpening ? SoundWorld.Instance.OpeningStrength : 0f);
            }
            else
            {
                em.ApplyPerception(
                    persisted.smoothedIntensity,
                    wallHits,
                    maxBounces,
                    muffle,
                    persisted.smoothedPoint);
            }

            SoundSignal signal = new SoundSignal(
                em.transform.position,
                persisted.smoothedDirection,
                persisted.smoothedIntensity,
                em.loudness,
                wallHits,
                persisted.smoothedDistance,
                em.category,
                result.id,
                em,
                muffle);

            signals[result.id] = signal;

            EmitSignal(em, signal, intensity);
        }

        // Persistência temporal
        foreach (SoundEmitter em in SoundWorld.Instance.Emitters)
        {
            if (em == null)
                continue;

            EntityId id = em.GetEntityId();

            if (updatedIds.Contains(id))
                continue;

            if (persistedPaths.TryGetValue(id, out PersistedPath persisted))
            {
                float age = now - persisted.lastSeenTime;

                if (age > persistenceTime)
                {
                    em.Silence();
                    continue;
                }

                float lifeT = 1f - age / persistenceTime;
                float decayedIntensity = persisted.intensity * lifeT * persistenceIntensityDecay;

                if (em.SourceType == SoundWorld.SoundSourceType.Global)
                {
                    em.ApplyGlobalPerception(
                        null,
                        maxBounces,
                        SoundWorld.Instance.IsOpenSpace,
                        SoundWorld.Instance.OpeningDirection,
                        SoundWorld.Instance.HasOpening ? SoundWorld.Instance.OpeningStrength : 0f);
                }
                else
                {
                    Vector3 fallbackPoint;

                    if (persisted.wallHits == 0)
                    {
                        fallbackPoint = em.transform.position;
                    }
                    else if (persisted.transmissionCollider != null)
                    {
                        fallbackPoint = SoundWorld.SafeClosestPoint(persisted.transmissionCollider, transform.position);
                    }
                    else if (persisted.hasReflectionPoint)
                    {
                        fallbackPoint = transform.position + persisted.smoothedDirection * persisted.smoothedDistance;
                    }
                    else
                    {
                        fallbackPoint = em.transform.position;
                    }

                    em.ApplyPerception(
                        decayedIntensity,
                        persisted.wallHits,
                        maxBounces,
                        persisted.transmissionMuffle,
                        fallbackPoint);
                }
            }
            else
            {
                em.Silence();
            }
        }
    }

    private void EmitSignal(SoundEmitter em, SoundSignal signal, float intensity)
    {
        if (intensity > minAudibleIntensity &&
            em.TryGetCaptionData(em.CurrentClip, out var caption) &&
            GameSettings.Instance.settingsData.showCaptions)
        {
            switch (caption.captionMode)
            {
                case SoundEmitter.CaptionMode.Once:
                    if (Time.time - em.lastCaptionTime >= em.captionCooldown && !em.CaptionShown)
                    {
                        em.lastCaptionTime = Time.time;
                        CaptionsManager.Instance?.ShowCaption(
                            caption.caption,
                            em.transform,
                            2f,
                            caption.captionPriority);
                    }
                    break;

                case SoundEmitter.CaptionMode.Repeat:
                    if (Time.time - em.lastCaptionTime >= em.captionCooldown &&
                        em.CanShowRepeatedCaption(2f))
                    {
                        em.lastCaptionTime = Time.time;
                        CaptionsManager.Instance?.ShowCaption(
                            caption.caption,
                            em.transform,
                            2f,
                            caption.captionPriority);
                    }
                    break;

                case SoundEmitter.CaptionMode.None:
                    break;
            }
        }

        bool forceGlyphs = SoundWorld.Instance != null && SoundWorld.Instance.DebugSettings.forceShowGlyphs;

        if (GameSettings.Instance.settingsData.showGlyphs || forceGlyphs)
            glyphRenderer?.RenderGlyph(signal);

        OnBestSignalReceived?.Invoke(signal);
    }
}