using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Profiling;
using Unity.VisualScripting;
using UnityEngine;

/// <summary>
/// Interruptores individuais para cada visualização/log de debug do sistema
/// de som, agrupados num único bloco pra não poluir o resto do Inspector.
/// Acessível em runtime via SoundWorld.Instance.DebugSettings.
/// </summary>
[Serializable]
public class SoundDebugSettings
{
    [Header("Raytrace")]
    [Tooltip("Raios que bateram numa parede (ricochete/absorção).")]
    public bool showBounceHits = false;

    [Tooltip("Raios que 'não bateram em nada' e CONTARAM como escape real (abertura/espaço aberto). Desenhados em verde.")]
    public bool showEscapeRays = false;

    [Tooltip("Mostrar raios que confirmam que um escape real existe (não bateu em nada por confirmEscapeDistance). Desenhados em vermelho.")]
    public bool showConfirmationRays = false;

    [Tooltip("Linha entre um ponto de probe (bounce) e o emissor testado a partir dele.")]
    public bool showProbeLinks = false;

    [Tooltip("Raio vermelho a partir do ouvinte mostrando a OpeningDirection média calculada (abertura detectada — janela/porta). Só desenha quando HasOpening é verdadeiro.")]
    public bool showOpeningDirection = false;

    [Tooltip("Comprimento do raio de debug da OpeningDirection, em metros.")]
    public float openingDirectionDebugLength = 5f;

    [Tooltip("Raios de REVERB do listener (independentes dos raios dos emissores). Ciano = segmento que bateu numa superfície (reflexão), laranja = segmento que escapou (espaço aberto).")]
    public bool showReverbRays = false;

    [Header("Listener")]
    [Tooltip("Linha do ouvinte até o ponto percebido de cada som (SoundListener).")]
    public bool showPerceivedDirectionLines = false;

    [Tooltip("Raio de obstrução direta (probePoint → emissor) usado em TestSingleEmitterAtPoint " +
             "pra decidir blockingMuffle/blockingTransmission de sons pontuais. Verde = não bateu em " +
             "nada (sem muffle de obstrução nesse segmento). Vermelho = bateu em algo sem SoundMaterial " +
             "(usa defaultTransmissionMuffle). Amarelo = bateu em algo COM SoundMaterial (usa os valores " +
             "do material). Se a porta fechada não aparecer nem vermelha nem amarela, o raio não está " +
             "colidindo com ela — provavelmente ela não está na wallMask, o collider está desabilitado, " +
             "ou é um trigger sendo ignorado pelas Physics queries.")]
    public bool showObstructionRays = false;

    [Header("Glyphs")]
    [Tooltip("Força os glyphs de acessibilidade a aparecer, ignorando a configuração de acessibilidade do jogador. Só pra debug.")]
    public bool forceShowGlyphs = false;

    [Header("Logging")]
    [Tooltip("Loga no console cada vez que um emissor global recebe uma nova percepção (quantos entries, se está em espaço aberto/abertura).")]
    public bool logGlobalPerception = false;
}

public class SoundWorld : MonoBehaviour
{
    [Header("Raytrace")]
    [Tooltip("Raios lançados POR FRAME. A cobertura completa da esfera ocorre em (totalRays / raysPerFrame) frames.")]
    [SerializeField] private int raysPerFrame = 64;

    [Tooltip("Total de raios na distribuição Fibonacci. Deve ser múltiplo de raysPerFrame para cobertura uniforme.")]
    [SerializeField] private int totalRays = 256;

    [Tooltip("Distância máxima total que um raio pode percorrer durante a simulação (bounces/escape). Usado só para decidir se um raio 'escapou' (Openness/aberturas) e até onde procurar pontos de reflexão — NÃO limita mais a audibilidade de um emissor; isso é controlado só por distanceFalloff/minAudibleIntensity.")]
    [SerializeField] private float maxDistance = 30f;

    [Tooltip("Número máximo de ricochetes por raio.")]
    [SerializeField] private int maxBounces = 3;

    [Tooltip("Layers de simulacao")]
    [SerializeField] private LayerMask wallMask;

    [Tooltip("Absorção usada quando a superfície atingida não tem SoundMaterial. " +
             "Fração de energia perdida a cada bounce (0 = reflexão perfeita, 1 = anecoica).")]
    [Range(0f, 1f)]
    [SerializeField] private float defaultAbsorption = 0.3f;

    [Tooltip("Muffle de transmissão usado quando a superfície atravessada/bloqueante não tem " +
             "SoundMaterial. Usado no raycast de obstrução do segmento final (TestSingleEmitterAtPoint) " +
             "sempre que uma superfície sem material bloqueia o caminho direto — esse é, na prática, " +
             "o único lugar onde este fallback é de fato alcançado: no branch de transmissão do " +
             "bounce-tracing (TraceRay), transmissionCoefficient também cai pra 0 quando não há " +
             "material, então effectiveTransmission nunca passa de 0 e aquele branch nem chega a " +
             "rodar — uma parede sem SoundMaterial nunca é tratada como transmissiva ali, só como " +
             "sólida (reflete ou bloqueia). Valor calibrado para bloqueio total (porta/parede sem " +
             "material tratada como praticamente opaca ao som).")]
    [Range(0f, 1f)]
    [SerializeField] private float defaultTransmissionMuffle = 1f;

    [Tooltip("Oclusao por parede (Porcentagem)")]
    [SerializeField] private float occlusionFactor = 0.8f;

    [Header("Transmission (espessura de parede)")]
    [Tooltip("O transmissionCoefficient de um SoundMaterial é calibrado para uma parede desta espessura (metros). Paredes mais grossas atenuam mais (exponencialmente); mais finas, menos.")]
    [SerializeField] private float wallThicknessReferenceMeters = 0.15f;

    [Tooltip("Distância máxima (metros) usada ao medir a espessura de uma parede via raycast de saída. Paredes mais grossas que isso usam o fallback abaixo.")]
    [SerializeField] private float maxWallThicknessProbeDistance = 5f;

    [Tooltip("Espessura assumida (metros) quando o raycast de saída não encontra a outra face (geometria não-convexa problemática, bordas, etc).")]
    [SerializeField] private float defaultWallThicknessFallback = 0.2f;

    [Tooltip("Se raios que escaparam via transmissão (atravessando paredes) devem contribuir para Openness/EscapedRayCount. DESLIGADO por padrão: a atenuação por espessura/material já vive inteiramente no cálculo por caminho (intensidade/muffle de cada GlobalEntry, em TestSingleEmitterAtPoint) — deixar transmissão também inflar Openness causa dupla contagem, já que Openness dispara um boost UNIFORME de volume/clareza em ApplyGlobalPerception (escapedVolume/hasOpening) por cima de todas as entries, mesmo as que não vieram de nenhuma abertura real. Openness deve representar só aberturas de verdade (visada direta, sem parede no caminho); transmissão deve influenciar apenas a intensidade/muffle do seu próprio caminho.")]
    [SerializeField] private bool transmissionCountsForOpenness = false;

    [Header("Probe Dedup")]
    [Tooltip("Tamanho da célula do grid para deduplicação de probe points (em metros). Pontos na mesma célula são ignorados.")]
    [SerializeField] private float probeCellSize = 1f;

    [Header("Indirect Intensity Blend")]
    [Tooltip("Fração da intensidade dos caminhos indiretos (reflexões) adicionada ao caminho principal. " +
             "0 = apenas melhor caminho; 1 = soma total. 0.35–0.5 é realista para ambientes fechados.")]
    [Range(0f, 1f)]
    [SerializeField] private float indirectIntensityBlend = 0.4f;

    [Header("Attenuation")]
    [Tooltip("Quão rápido a intensidade cai com a distância percorrida. Aproxima a lei do inverso do quadrado da vida real (1 / (1 + distância * distanceFalloff)²) — dobrar a distância derruba a intensidade a ~1/4, não ~1/2.")]
    [SerializeField] private float distanceFalloff = 0.12f;

    [Tooltip("Intensidade mínima para um som ser considerado audível; abaixo disso o caminho é descartado.")]
    [SerializeField] private float minAudibleIntensity = 0.02f;

    [Tooltip("Energia mínima para não descartar transmissão")]
    [SerializeField] private float minPathEnergy = 0.005f;

    [Header("Muffling")]
    [Tooltip("Quão rápido o som perde agudos (0 = cristalino, 1 = totalmente abafado) conforme a distância percorrida cresce, mesmo em visada direta — aproxima a absorção real do ar, que come frequências altas primeiro. Curva de saturação (1 - e^(-distância * distanceMuffleRate)): sobe rápido perto da fonte e desacelera longe. Este muffling é a BASE de todo caminho; muffling de transmissão (parede) e de reflexão são somados em cima dele. CUIDADO: por causa do mapeamento log (22000Hz→600Hz) usado pra converter muffle em cutoff, valores pequenos aqui já têm bastante efeito perceptual — 0.01 já deixa um som a 30m nitidamente mais escuro que um bem perto.")]
    [SerializeField] private float distanceMuffleRate = 0.01f;

    [Tooltip("Muffling (0-1) somado quando o som chega por reflexão (bounce numa superfície) em vez de visada direta — uma reflexão difusa/especular perde agudos além do que a distância já tira, mesmo sem nenhuma parede bloqueando de fato o caminho. Somado por cima do muffling de distância; se a reflexão TAMBÉM passar por uma parede (transmissão), o muffling da parede é somado além deste.")]
    [Range(0f, 1f)]
    [SerializeField] private float reflectionMuffle = 0.3f;

    [Header("Openness Detection")]
    [Tooltip("Fração (0-1) de raios que precisam 'escapar' (chegar ao fim sem colidir) para o espaço ser considerado aberto. Acima disso, o som global toca em mono.")]
    [Range(0f, 1f)]
    [SerializeField] private float openSpaceThreshold = 0.45f;

    [Tooltip("Concentração mínima (0-1) dos raios escapados para considerar que existe uma abertura única (janela/porta) em vez de um espaço aberto difuso.")]
    [Range(0f, 1f)]
    [SerializeField] private float openingConcentrationThreshold = 0.6f;

    [Tooltip("Fração mínima de raios escapados para sequer considerar uma abertura (evita ruído com 1-2 raios perdidos).")]
    [Range(0f, 1f)]
    [SerializeField] private float minEscapedFractionForOpening = 0.03f;

    [Tooltip("Raios com até esse número de ricochetes que escapam contam como candidatos a 'saída direta' (janela). Raios com mais bounces contam para o quão aberto o espaço é, mas pesam menos na direção da abertura.")]
    [SerializeField] private int maxWallHitsForOpening = 1;

    [Tooltip("Fração (0-1) de maxDistance que um raio precisa ter DISPONÍVEL para percorrer, e ainda assim não bater em nada, para contar como escape real. Evita falso-positivo quando o raio só ficou sem orçamento de simulação perto de uma parede depois de vários bounces (o que faria uma casa fechada parecer 'aberta').")]
    [Range(0f, 1f)]
    [SerializeField] private float minEscapeSegmentFraction = 0.5f;

    [Header("Global Openness")]
    [Tooltip("Velocidade de transição entre som espacializado (3D) e mono, ao entrar/sair de espaços abertos.")]
    public float spatialBlendLerpSpeed = 4f;

    [Tooltip("Ângulo máximo (graus) entre a direção de um raio escapado e o centróide de um cluster de abertura pra esse raio entrar nesse cluster. Evita que aberturas em direções opostas se cancelem na média — cada uma vira seu próprio cluster.")]
    [Range(1f, 90f)]
    [SerializeField] private float openingClusterAngleThreshold = 40f;

    [Tooltip("Se o cluster de abertura dominante for formado por pelo menos 1 raio de visada direta (sem bounce), a abertura é considerada válida independente da fração total de raios escapados (Openness). Sem isso, janelas pequenas — onde só 1-2 raios de 256 escapam — nunca disparam HasOpening, já que Openness fica abaixo de minEscapedFractionForOpening mesmo com evidência forte e confiável de que a abertura existe.")]
    [SerializeField] private bool trustDirectClusterRegardlessOfOpenness = true;

    [Tooltip("Distância usada para confirmar que um raio escapado realmente saiu do ambiente (não bateu em nada) antes de contar como abertura. Evita falso-positivo quando o raio só ficou sem orçamento de simulação perto de uma parede depois de vários bounces.")]
    [SerializeField] private float confirmEscapeDistance = 100f;

    [Header("Reverb (raios próprios do listener)")]
    [Tooltip("Raios de reverb lançados POR FRAME de processamento. Totalmente independentes dos raios de raytrace dos emissores — o reverb é a percepção do AMBIENTE ao redor do listener, não de um som específico.")]
    [SerializeField] private int reverbRaysPerFrame = 32;

    [Tooltip("Total de raios na distribuição Fibonacci do reverb. Cobertura completa em (reverbTotalRays / reverbRaysPerFrame) ticks.")]
    [SerializeField] private int reverbTotalRays = 128;

    [Tooltip("Número máximo de reflexões que cada raio de reverb segue. Mais reflexões = estimativa melhor de ambientes grandes/complexos, a um custo de 1 raycast por reflexão.")]
    [SerializeField] private int reverbMaxBounces = 4;

    [Tooltip("Distância máxima TOTAL por raio de reverb. Um segmento que não bate em nada com pelo menos metade desse orçamento sobrando conta como 'escape' (espaço aberto): a energia some e o reverb diminui.")]
    [SerializeField] private float reverbMaxDistance = 40f;

    [Tooltip("Velocidade do som (m/s), usada para converter caminho livre médio em tempo entre reflexões.")]
    [SerializeField] private float speedOfSound = 343f;

    [Tooltip("Tempo de decaimento (RT60, segundos) mínimo e máximo devolvidos ao mixer.")]
    [SerializeField] private Vector2 reverbDecayRange = new Vector2(0.2f, 6f);

    [Tooltip("Energia retida por reflexão (0-1) que mapeia para wet = 0 (x) e wet = 1 (y). Retenção = P(raio bate em algo) × (1 − absorção média). Salas bem absorventes/abertas ficam perto de x, salas duras e fechadas perto de y.")]
    [SerializeField] private Vector2 reverbRetentionRange = new Vector2(0.2f, 0.85f);

    [Header("Debug")]
    [Tooltip("Controla individualmente quais visualizações de debug ficam ativas.")]
    [SerializeField] private SoundDebugSettings debugSettings = new SoundDebugSettings();

    public SoundDebugSettings DebugSettings => debugSettings;

    public int MaxBounces => maxBounces;

    public static SoundWorld Instance { get; private set; }

    private bool[] rayEscaped;
    private Vector3[] rayEscapedDirection;
    private int[] rayEscapedWallHits;

    private readonly RaycastHit[] raycastBuffer = new RaycastHit[16];

    /// <summary>Fração (0-1) de raios do sweep completo que escaparam sem colidir.</summary>
    public float Openness { get; private set; }

    /// <summary>Direção média (do ouvinte para fora) da abertura dominante, se houver.</summary>
    public Vector3 OpeningDirection { get; private set; }

    /// <summary>Quão concentrados estão os raios escapados nessa direção (0 = difuso, 1 = um feixe único).</summary>
    public float OpeningStrength { get; private set; }

    /// <summary>Quantos raios (ponderados por peso, não contagem bruta) formam o cluster de abertura dominante.</summary>
    public float DominantClusterWeight { get; private set; }

    /// <summary>Verdadeiro se o cluster dominante contém pelo menos um raio de visada direta (wallHits == 0) — evidência forte de abertura real, não artefato de reflexão.</summary>
    public bool DominantClusterIsDirect { get; private set; }

    /// <summary>Espaço totalmente aberto (ex.: campo aberto) — som global deve tocar em mono.</summary>
    public bool IsOpenSpace =>
        Openness > openSpaceThreshold && OpeningStrength < openingConcentrationThreshold;

    /// <summary>Ambiente fechado com uma abertura clara (ex.: janela) por onde o som externo vaza.</summary>
    public bool HasOpening =>
    !IsOpenSpace &&
    (Openness > minEscapedFractionForOpening ||
     (trustDirectClusterRegardlessOfOpenness && DominantClusterIsDirect));

    public struct ReverbData
    {
        /// <summary>0-1: quão "molhado" deve ser o reverb (depende de enclausuramento e absorção).</summary>
        public float wetLevel;

        /// <summary>RT60 estimado em segundos (fórmula de Eyring a partir dos raios).</summary>
        public float decayTime;

        /// <summary>0-1: caminho livre médio relativo a reverbMaxDistance (0 = closet, 1 = salão enorme).</summary>
        public float roomSize;

        /// <summary>0-1: fração dos segmentos de raio que bateram em alguma superfície (1 = totalmente fechado).</summary>
        public float enclosure;

        /// <summary>Distância média (m) entre reflexões.</summary>
        public float meanFreePath;

        /// <summary>Absorção média (0-1) das superfícies atingidas.</summary>
        public float meanAbsorption;
    }

    /// <summary>Reverb atual do listener (sem suavização — suavize em quem consome).</summary>
    public ReverbData Reverb { get; private set; }

    private float[] reverbRayLengthSum;
    private float[] reverbRayAbsorptionSum;
    private int[] reverbRayHits;
    private int[] reverbRayEscapes;
    private int _reverbRayOffset;

    private const float ReverbEscapeBudgetFraction = 0.5f;
    // ln(1e-6): queda de 60 dB em energia.
    private const float Ln60dB = -13.8155106f;

    private readonly List<SoundEmitter> emitters = new();
    private readonly List<SoundResult> resultsCache = new();

    public int EscapedRayCount { get; private set; } = 0;

    private struct AccumulatedPath
    {
        public float intensitySum;
        public float wallHitsWeightedSum;
        public SoundEmitter emitter;
        public List<DirectionSample> directionSamples;
        public float bestIntensity;
        public Vector3 bestPerceivedDirection;
        public Vector3 bestPerceivedPoint;
        public int bestWallHits;
        public float bestDistance;
        public float bestMuffle;
        public Collider bestTransmissionCollider;
        public bool bestHasReflectionPoint;
        public List<GlobalEntry> globalEntries;
    }

    private struct RayState
    {
        public Vector3 origin;
        public Vector3 direction;
        public float traveledDistance;
        public int wallHits;
        public int reflectionBounces;
        public float transmissionMuffle;
        public float energy;
        public bool usedTransmission;
        public Vector3 lastDirectPoint;
        public bool hasDirectPoint;
        public Collider transmissionCollider;
    }

    public struct GlobalEntry
    {
        public Vector3 point;
        public Vector3 direction;

        public float intensity;
        public float muffle;

        public int wallHits;
    }

    private struct DirectionSample
    {
        public Vector3 direction;
        public Vector3 point;
        public float intensity;
        public float muffle;
    }

    public struct SoundResult
    {
        public SoundEmitter emitter;
        public EntityId id;

        // usado apenas para sons pontuais
        public Vector3 perceivedPoint;
        public Vector3 perceivedDirection;

        public float intensity;
        public float distance;

        public int wallHits;
        public float transmissionMuffle;

        public Collider transmissionCollider;

        public bool hasReflectionPoint;

        // usado apenas para sons globais
        public List<GlobalEntry> globalEntries;
    }

    /// <summary>Um agrupamento de raios escapados com direção parecida — representa uma abertura candidata (janela/porta/etc).</summary>
    public struct OpeningCluster
    {
        public Vector3 direction;      // direção média normalizada do cluster
        public float weight;           // soma dos pesos dos raios que caíram nesse cluster
        public float concentration;    // 0-1, quão alinhados estão os raios dentro do próprio cluster
        public int rayCount;
    }

    private struct ProbeSample
    {
        public Vector3 point;
        public float traveledSoFar;
        public int wallHits;
        public int reflectionBounces;
        public float transmissionMuffle;
        public float energySoFar;
        public Collider transmissionCollider;

        public Vector3 lastDirectPoint;
        public bool hasDirectPoint;
    }

    private struct OpeningClusterAccum
    {
        public Vector3 dirSum;
        public float weightSum;
        public int rayCount;
        public bool allDirect;
    }

    private readonly List<OpeningClusterAccum> openingClusterAccums = new(8);
    private readonly List<OpeningCluster> openingClusters = new(8);

    private const int MaxCachedProbesPerRay = 4;
    private List<ProbeSample>[] cachedProbesByRay;

    /// <summary>Todos os clusters de abertura detectados nesse frame.</summary>
    public IReadOnlyList<OpeningCluster> OpeningClusters => openingClusters;

    private readonly RayState[] bounceStack = new RayState[32];

    private float[] rayEscapedEnergy;

    private readonly Dictionary<EntityId, AccumulatedPath> frameAccumulated = new();

    public List<SoundEmitter> Emitters => emitters;

    public enum SoundSourceType
    {
        Point,
        Global
    }

    private readonly HashSet<long> visitedCells = new();

    private int _rayOffset;

    private readonly List<SoundEmitter> activeEmitters = new();
    private const float cullSlack = 2f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        rayEscaped = new bool[totalRays];
        rayEscapedDirection = new Vector3[totalRays];
        rayEscapedWallHits = new int[totalRays];
        rayEscapedEnergy = new float[totalRays];
        cachedProbesByRay = new List<ProbeSample>[totalRays];

        reverbRayLengthSum = new float[reverbTotalRays];
        reverbRayAbsorptionSum = new float[reverbTotalRays];
        reverbRayHits = new int[reverbTotalRays];
        reverbRayEscapes = new int[reverbTotalRays];

        for (int i = 0; i < totalRays; i++)
            cachedProbesByRay[i] = new List<ProbeSample>(MaxCachedProbesPerRay);

        foreach (var emitter in FindObjectsByType<SoundEmitter>())
            RegisterEmitter(emitter);
    }

    public void RegisterEmitter(SoundEmitter emitter)
    {
        if (!emitters.Contains(emitter))
        {
            emitters.Add(emitter);
        }
    }


    public void UnregisterEmitter(SoundEmitter emitter)
    {
        emitters.Remove(emitter);
    }

    private Vector3 lastListenerPosition;

    public List<SoundResult> Process(Vector3 listenerPosition)
    {
        frameAccumulated.Clear();
        visitedCells.Clear();
        lastListenerPosition = listenerPosition;
        BuildActiveEmitters(listenerPosition);
        CastListenerRays(listenerPosition);
        AccumulateFromProbeCache(listenerPosition);
        ComputeOpenness();
        UpdateReverb(listenerPosition);

        return BuildResults();
    }

    /// <summary>
    /// Atualiza SÓ o reverb do listener, sem raytrace de emissores/openness.
    /// Usado pelo SoundListener quando GameSettings.UseRayTracing está desligado
    /// (C0/C1) mas o reverb ainda pode estar ligado de forma independente.
    /// </summary>
    public void ProcessReverbOnly(Vector3 listenerPosition)
    {
        lastListenerPosition = listenerPosition;
        UpdateReverb(listenerPosition);
    }

    private void UpdateReverb(Vector3 listenerPosition)
    {
        if (GameSettings.Instance != null && !GameSettings.Instance.UseListenerReverb)
        {
            Reverb = DryReverb();
            return;
        }

        CastReverbRays(listenerPosition);
        ComputeReverb();
    }

    private ReverbData DryReverb()
    {
        return new ReverbData
        {
            wetLevel = 0f,
            decayTime = reverbDecayRange.x,
            roomSize = 0f,
            enclosure = 0f,
            meanFreePath = 0f,
            meanAbsorption = 0f,
        };
    }

    private void ComputeOpenness()
    {
        float totalEscapedEnergy = 0f;

        EscapedRayCount = 0;
        openingClusterAccums.Clear();

        bool anyDirectEscape = false;
        float clusterCosThreshold = Mathf.Cos(openingClusterAngleThreshold * Mathf.Deg2Rad);

        for (int i = 0; i < totalRays; i++)
        {
            if (!rayEscaped[i]) continue;

            EscapedRayCount++;
            totalEscapedEnergy += rayEscapedEnergy[i];

            if (rayEscapedWallHits[i] == 0)
                anyDirectEscape = true;
        }

        int wallHitsCutoff = anyDirectEscape ? 0 : maxWallHitsForOpening;

        for (int i = 0; i < totalRays; i++)
        {
            if (!rayEscaped[i]) continue;
            if (rayEscapedWallHits[i] > wallHitsCutoff) continue;

            Vector3 dir = rayEscapedDirection[i];
            if (dir.sqrMagnitude < 0.0001f) continue;

            float weight = 1f / (1f + rayEscapedWallHits[i]);

            int bestCluster = -1;
            float bestDot = clusterCosThreshold;

            for (int c = 0; c < openingClusterAccums.Count; c++)
            {
                OpeningClusterAccum candidate = openingClusterAccums[c];
                if (candidate.weightSum <= 0f) continue;

                Vector3 centroid = candidate.dirSum / candidate.weightSum;
                if (centroid.sqrMagnitude < 0.0001f) continue;

                float dot = Vector3.Dot(dir, centroid.normalized);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    bestCluster = c;
                }
            }

            if (bestCluster >= 0)
            {
                OpeningClusterAccum acc = openingClusterAccums[bestCluster];
                acc.dirSum += dir * weight;
                acc.weightSum += weight;
                acc.rayCount++;
                acc.allDirect = acc.allDirect && rayEscapedWallHits[i] == 0;
                openingClusterAccums[bestCluster] = acc;
            }
            else
            {
                openingClusterAccums.Add(new OpeningClusterAccum
                {
                    dirSum = dir * weight,
                    weightSum = weight,
                    rayCount = 1,
                    allDirect = rayEscapedWallHits[i] == 0,
                });
            }
        }

        Openness = totalRays > 0 ? (float)EscapedRayCount / totalRays : 0f;

        openingClusters.Clear();

        int dominant = -1;
        float dominantWeight = 0f;
        float totalClusterWeight = 0f;

        for (int c = 0; c < openingClusterAccums.Count; c++)
        {
            OpeningClusterAccum acc = openingClusterAccums[c];
            if (acc.weightSum <= 0.001f) continue;

            totalClusterWeight += acc.weightSum;

            openingClusters.Add(new OpeningCluster
            {
                direction = acc.dirSum.normalized,
                weight = acc.weightSum,
                concentration = acc.dirSum.magnitude / acc.weightSum,
                rayCount = acc.rayCount,
            });

            if (acc.weightSum > dominantWeight)
            {
                dominantWeight = acc.weightSum;
                dominant = openingClusters.Count - 1;
            }
        }

        if (dominant >= 0)
        {
            OpeningCluster winner = openingClusters[dominant];
            OpeningClusterAccum winnerAcc = openingClusterAccums[dominant];

            OpeningDirection = winner.direction;
            OpeningStrength = totalClusterWeight > 0.001f
                ? dominantWeight / totalClusterWeight
                : 0f;

            DominantClusterWeight = winnerAcc.weightSum;
            DominantClusterIsDirect = winnerAcc.allDirect;
        }
        else
        {
            OpeningDirection = Vector3.zero;
            OpeningStrength = 0f;
            DominantClusterWeight = 0f;
            DominantClusterIsDirect = false;
        }

#if UNITY_EDITOR
        if (debugSettings.showOpeningDirection && HasOpening)
        {
            Debug.DrawLine(
                lastListenerPosition,
                lastListenerPosition + OpeningDirection * debugSettings.openingDirectionDebugLength,
                Color.red,
                0.15f);
        }
#endif
    }

    private void CastReverbRays(Vector3 listenerPosition)
    {
        if (reverbTotalRays <= 0) return;

        int end = _reverbRayOffset + reverbRaysPerFrame;
        for (int i = _reverbRayOffset; i < end; i++)
        {
            int idx = i % reverbTotalRays;
            TraceReverbRay(listenerPosition, FibonacciSphereDirection(idx, reverbTotalRays), idx);
        }

        _reverbRayOffset = (_reverbRayOffset + reverbRaysPerFrame) % reverbTotalRays;
    }

    private void TraceReverbRay(Vector3 origin, Vector3 direction, int rayIndex)
    {
        float lengthSum = 0f;
        float absorptionSum = 0f;
        int hits = 0;
        int escapes = 0;
        float traveled = 0f;

        for (int b = 0; b < reverbMaxBounces; b++)
        {
            float remaining = reverbMaxDistance - traveled;
            if (remaining <= 0.05f) break; // orçamento esgotado: nem bate, nem escapa

            if (!Physics.Raycast(origin, direction, out RaycastHit hit, remaining, wallMask))
            {
                // Só conta como espaço aberto se o raio tinha orçamento de sobra;
                // senão ele só ficou sem alcance perto de uma parede distante.
                if (remaining >= reverbMaxDistance * ReverbEscapeBudgetFraction)
                {
                    escapes++;
#if UNITY_EDITOR
                    if (debugSettings.showReverbRays)
                        Debug.DrawLine(origin, origin + direction * remaining, new Color(1f, 0.6f, 0f, 0.4f), 0.15f);
#endif
                }
                break;
            }

#if UNITY_EDITOR
            if (debugSettings.showReverbRays)
                Debug.DrawLine(origin, hit.point, new Color(0f, 1f, 1f, 0.35f), 0.15f);
#endif

            SoundMaterial material = GetCachedMaterial(hit.collider);
            float absorption = material != null ? material.absorptionCoefficient : defaultAbsorption;
            float scattering = material != null ? material.scattering : 0f;

            hits++;
            lengthSum += hit.distance;
            absorptionSum += Mathf.Clamp01(absorption);
            traveled += hit.distance;

            Vector3 reflected = Vector3.Reflect(direction, hit.normal);
            if (scattering > 0f)
            {
                Vector3 rand = UnityEngine.Random.insideUnitSphere;
                if (Vector3.Dot(rand, hit.normal) < 0f) rand = -rand;
                reflected = Vector3.Slerp(reflected, rand, scattering).normalized;
            }

            origin = hit.point + hit.normal * 0.05f;
            direction = reflected;
        }

        reverbRayLengthSum[rayIndex] = lengthSum;
        reverbRayAbsorptionSum[rayIndex] = absorptionSum;
        reverbRayHits[rayIndex] = hits;
        reverbRayEscapes[rayIndex] = escapes;
    }

    private void ComputeReverb()
    {
        float lengthSum = 0f;
        float absorptionSum = 0f;
        int hits = 0;
        int escapes = 0;

        for (int i = 0; i < reverbTotalRays; i++)
        {
            lengthSum += reverbRayLengthSum[i];
            absorptionSum += reverbRayAbsorptionSum[i];
            hits += reverbRayHits[i];
            escapes += reverbRayEscapes[i];
        }

        int segments = hits + escapes;

        // Nada refletiu (campo aberto total ou sweep ainda sem dados): seco.
        if (hits == 0 || segments == 0)
        {
            Reverb = new ReverbData
            {
                wetLevel = 0f,
                decayTime = reverbDecayRange.x,
                roomSize = 0f,
                enclosure = 0f,
                meanFreePath = 0f,
                meanAbsorption = 0f,
            };
            return;
        }

        float enclosure = (float)hits / segments;
        float meanFreePath = lengthSum / hits;
        float meanAbsorption = absorptionSum / hits;

        float retention = Mathf.Clamp(enclosure * (1f - meanAbsorption), 0.001f, 0.999f);

        float bounceInterval = meanFreePath / Mathf.Max(1f, speedOfSound);
        float bouncesTo60dB = Ln60dB / Mathf.Log(retention);
        float decay = Mathf.Clamp(bouncesTo60dB * bounceInterval, reverbDecayRange.x, reverbDecayRange.y);

        Reverb = new ReverbData
        {
            wetLevel = Mathf.Clamp01(Mathf.InverseLerp(reverbRetentionRange.x, reverbRetentionRange.y, retention)),
            decayTime = decay,
            roomSize = Mathf.Clamp01(meanFreePath / Mathf.Max(0.01f, reverbMaxDistance)),
            enclosure = enclosure,
            meanFreePath = meanFreePath,
            meanAbsorption = meanAbsorption,
        };
    }

    private void CastListenerRays(Vector3 listenerPosition)
    {
        TestEmittersFromPoint(listenerPosition, 0f, 0, 0f, 1f, listenerPosition, null, listenerPosition, true, 0);

        int end = _rayOffset + raysPerFrame;
        for (int i = _rayOffset; i < end; i++)
        {
            int idx = i % totalRays;
            Vector3 dir = FibonacciSphereDirection(idx, totalRays);
            TraceRay(listenerPosition, dir, idx);
        }

        _rayOffset = (_rayOffset + raysPerFrame) % totalRays;
    }

    private void TraceRay(Vector3 listenerPosition, Vector3 startDirection, int rayIndex)
    {
        int stackSize = 0;
        bounceStack[stackSize++] = new RayState
        {
            origin = listenerPosition,
            direction = startDirection,
            traveledDistance = 0f,
            wallHits = 0,
            reflectionBounces = 0,
            transmissionMuffle = 0f,
            energy = 1f,
            usedTransmission = false,
            lastDirectPoint = listenerPosition,
            hasDirectPoint = true,
            transmissionCollider = null,
        };

        var cache = cachedProbesByRay[rayIndex];
        cache.Clear();

        // Reseta o estado de escape deste índice de raio para o sweep atual —
        // se nenhum dos ramos (reflexão/transmissão) escapar desta vez, o
        // índice volta a ser "não escapado" (ex.: uma porta que fechou).
        bool escaped = false;
        int escapedWallHits = int.MaxValue;
        float escapedEnergy = 0f;

        float escapedEnergySum = 0f;

        Vector3 escapedDirectionSum = Vector3.zero;
        float escapedDirectionWeight = 0f;

        while (stackSize > 0)
        {
            RayState state = bounceStack[--stackSize];

            if (state.wallHits > maxBounces) continue;
            if (state.traveledDistance >= maxDistance) continue;

            float remaining = maxDistance - state.traveledDistance;

            //guarda o ponto pro cache, capado por energia (mais forte sobrevive)
            AddToProbeCache(cache, new ProbeSample
            {
                point = state.origin,
                traveledSoFar = state.traveledDistance,
                wallHits = state.wallHits,
                reflectionBounces = state.reflectionBounces,
                transmissionMuffle = state.transmissionMuffle,
                energySoFar = state.energy,
                transmissionCollider = state.transmissionCollider,
                lastDirectPoint = state.lastDirectPoint,
                hasDirectPoint = state.hasDirectPoint,
            });

            Ray ray = new Ray(state.origin, state.direction);
            bool hit = Physics.Raycast(ray, out RaycastHit hitInfo, remaining, wallMask);

            if (!hit)
            {
                bool countsAsEscape =
                    (transmissionCountsForOpenness || !state.usedTransmission) &&
                    remaining >= maxDistance * minEscapeSegmentFraction;

#if UNITY_EDITOR
                if (countsAsEscape && debugSettings.showEscapeRays)
                {
                    Debug.DrawLine(
                        state.origin,
                        state.origin + state.direction * remaining,
                        new Color(0f, 1f, 0f, 0.6f));
                }
#endif
                if (countsAsEscape)
                {
                    escaped = true;

                    bool reallyEscaped = !Physics.Raycast(state.origin, state.direction, confirmEscapeDistance, wallMask);

#if UNITY_EDITOR
                    if (debugSettings.showConfirmationRays)
                    {
                        Debug.DrawLine(
                            state.origin,
                            state.origin + state.direction * confirmEscapeDistance,
                            reallyEscaped ? Color.green : Color.yellow);
                    }
#endif

                    if (!reallyEscaped)
                    {
                        countsAsEscape = false;
                        continue; // não escapou de verdade — não conta, próxima iteração do while
                    }

                    bool betterEscape =
                        state.energy > escapedEnergy ||
                        (
                            Mathf.Approximately(state.energy, escapedEnergy) &&
                            state.wallHits < escapedWallHits
                        );

                    if (betterEscape)
                    {
                        escapedWallHits = state.wallHits;
                        escapedEnergy = state.energy;
                    }

                    Vector3 perceivedDirection;

                    if (state.wallHits == 0)
                    {
                        // O próprio raio saiu diretamente.
                        perceivedDirection = state.direction;
                    }
                    else if (state.hasDirectPoint)
                    {
                        // Último ponto do caminho que possui LOS com o jogador.
                        Vector3 toDirectPoint =
                            state.lastDirectPoint - listenerPosition;

                        if (toDirectPoint.sqrMagnitude <= 0.0001f)
                            continue;

                        perceivedDirection = toDirectPoint.normalized;
                    }
                    else
                    {
                        // Escapou, mas nenhum ponto do caminho possui LOS.
                        continue;
                    }

                    escapedDirectionSum += perceivedDirection * state.energy;
                    escapedDirectionWeight += state.energy;

                    // A energia deste escape, e não a energia de qualquer bounce.
                    escapedEnergySum += state.energy;
                }
                continue;
            }

#if UNITY_EDITOR
            if (debugSettings.showBounceHits)
                Debug.DrawLine(state.origin, hitInfo.point, new Color(0.4f, 0.8f, 0.4f, 0.25f));
#endif

            float hitDist = state.traveledDistance + hitInfo.distance;

            bool directToListener = HasDirectLineOfSight(hitInfo.point, listenerPosition);

            Vector3 lastDirectPoint = state.lastDirectPoint;
            bool hasDirectPoint = state.hasDirectPoint;

            if (directToListener)
            {
                lastDirectPoint = hitInfo.point;
                hasDirectPoint = true;
            }

            SoundMaterial material = GetCachedMaterial(hitInfo.collider);
            float scattering = material != null ? material.scattering : 0f;
            float transmission = material != null ? material.transmissionCoefficient : 0f;
            float muffle = material != null ? material.transmissionMuffle : defaultTransmissionMuffle;
            float absorption = material != null ? material.absorptionCoefficient : defaultAbsorption;
            float effectiveTransmission = 0f;

            if (transmission > 0f)
            {
                float wallThickness = MeasureWallThickness(hitInfo, state.direction);
                float thicknessRatio = wallThickness / Mathf.Max(0.001f, wallThicknessReferenceMeters);
                effectiveTransmission = Mathf.Pow(Mathf.Clamp01(transmission), thicknessRatio);
            }

            // Raio refletido
            if (stackSize < bounceStack.Length - 1)
            {
                Vector3 reflected = Vector3.Reflect(state.direction, hitInfo.normal);

                if (scattering > 0f)
                {
                    Vector3 rand = UnityEngine.Random.insideUnitSphere;
                    if (Vector3.Dot(rand, hitInfo.normal) < 0f) rand = -rand;
                    reflected = Vector3.Slerp(reflected, rand, scattering).normalized;
                }

                bounceStack[stackSize++] = new RayState
                {
                    origin = hitInfo.point + hitInfo.normal * 0.05f,
                    direction = reflected,
                    traveledDistance = hitDist,
                    wallHits = state.wallHits + 1,
                    reflectionBounces = state.reflectionBounces + 1,
                    transmissionMuffle = state.transmissionMuffle,
                    energy = state.energy * (1f - absorption),
                    usedTransmission = state.usedTransmission,
                    lastDirectPoint = lastDirectPoint,
                    hasDirectPoint = hasDirectPoint,
                    transmissionCollider = state.transmissionCollider,
                };
            }

            // Raio de transmissão
            if (effectiveTransmission > 0f && state.energy * effectiveTransmission >= minPathEnergy && stackSize < bounceStack.Length - 1)
            {
                bounceStack[stackSize++] = new RayState
                {
                    origin = hitInfo.point + state.direction * 0.05f,
                    direction = state.direction,
                    traveledDistance = hitDist,
                    wallHits = state.wallHits + 1,
                    reflectionBounces = state.reflectionBounces,
                    transmissionMuffle = Mathf.Min(1f, state.transmissionMuffle + muffle),
                    energy = state.energy * effectiveTransmission,
                    usedTransmission = true,
                    lastDirectPoint = lastDirectPoint,
                    hasDirectPoint = hasDirectPoint,
                    transmissionCollider = hitInfo.collider,
                };
            }
        }

        Vector3 averageEscapedDirection = Vector3.zero;

        if (escapedDirectionWeight > 0.001f)
        {
            averageEscapedDirection =
                (escapedDirectionSum / escapedDirectionWeight).normalized;
        }

        rayEscaped[rayIndex] = escaped;

        if (escaped)
        {
            rayEscapedDirection[rayIndex] = averageEscapedDirection;
            rayEscapedWallHits[rayIndex] = escapedWallHits;
            rayEscapedEnergy[rayIndex] = escapedEnergySum;
        }
        else
        {
            rayEscapedDirection[rayIndex] = Vector3.zero;
            rayEscapedWallHits[rayIndex] = 0;
            rayEscapedEnergy[rayIndex] = 0f;
        }
    }

    private void TestEmittersFromPoint(
        Vector3 probePoint,
        float traveledSoFar,
        int wallHitsSoFar,
        float muffleSoFar,
        float energySoFar,
        Vector3 listenerPosition,
        Collider transmissionCollider = null,
        Vector3 lastDirectPoint = default,
        bool hasDirectPoint = false,
        int reflectionBouncesSoFar = 0)
    {
        long cellKey = ProbeCell(probePoint);
        if (!visitedCells.Add(cellKey)) return;

        for (int e = 0; e < emitters.Count; e++)
        {
            SoundEmitter emitter = emitters[e];
            if (emitter == null) continue;
            TestSingleEmitterAtPoint(emitter, probePoint, traveledSoFar, wallHitsSoFar, muffleSoFar, energySoFar, listenerPosition, transmissionCollider, lastDirectPoint, hasDirectPoint, reflectionBouncesSoFar);
        }
    }

    private List<SoundResult> BuildResults()
    {
        resultsCache.Clear();

        foreach (var kvp in frameAccumulated)
        {
            AccumulatedPath acc = kvp.Value;

            if (acc.intensitySum <= Mathf.Epsilon)
                continue;


            float intensity = acc.bestIntensity +
                  (acc.intensitySum - acc.bestIntensity) * indirectIntensityBlend;


            int wallHits =
                acc.intensitySum > Mathf.Epsilon ?
                Mathf.RoundToInt(
                    acc.wallHitsWeightedSum / acc.intensitySum) :
                acc.bestWallHits;

            Vector3 finalDirection = acc.bestPerceivedDirection;
            Vector3 finalPoint = acc.bestPerceivedPoint;
            float muffle = acc.bestMuffle;

            if (acc.directionSamples != null && acc.directionSamples.Count > 0)
            {
                Vector3 dirSum = Vector3.zero;
                Vector3 pointSum = Vector3.zero;
                float muffleSum = 0f;
                float weightSum = 0f;

                for (int s = 0; s < acc.directionSamples.Count; s++)
                {
                    DirectionSample sample = acc.directionSamples[s];
                    dirSum += sample.direction * sample.intensity;
                    pointSum += sample.point * sample.intensity;
                    muffleSum += sample.muffle * sample.intensity;
                    weightSum += sample.intensity;
                }

                if (weightSum > Mathf.Epsilon)
                {
                    finalDirection = (dirSum / weightSum).normalized;
                    finalPoint = pointSum / weightSum;
                    muffle = muffleSum / weightSum;
                }
            }

            if (acc.globalEntries != null)
            {
                acc.globalEntries.Sort(
                    (a, b) => b.intensity.CompareTo(a.intensity));

                if (acc.globalEntries.Count > 4)
                    acc.globalEntries.RemoveRange(
                        4,
                        acc.globalEntries.Count - 4);
            }

            resultsCache.Add(new SoundResult
            {
                emitter = acc.emitter,
                id = kvp.Key,

                perceivedPoint = finalPoint,
                perceivedDirection = finalDirection,

                intensity = intensity,
                distance = acc.bestDistance,

                wallHits = wallHits,
                transmissionMuffle = muffle,
                transmissionCollider = acc.bestTransmissionCollider,
                hasReflectionPoint = acc.bestHasReflectionPoint,

                globalEntries = acc.globalEntries
            });
        }

        return resultsCache;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Utilitários
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Retorna um índice de célula 3D único para deduplicação de probe points.
    /// Usa long para evitar overflow com coordenadas negativas.
    /// </summary>
    /// 
    private static void AddToProbeCache(List<ProbeSample> cache, ProbeSample sample)
    {
        if (cache.Count < MaxCachedProbesPerRay)
        {
            cache.Add(sample);
            return;
        }

        int weakest = 0;
        for (int i = 1; i < cache.Count; i++)
            if (cache[i].energySoFar < cache[weakest].energySoFar)
                weakest = i;

        if (sample.energySoFar > cache[weakest].energySoFar)
            cache[weakest] = sample;
    }

    private long ProbeCell(Vector3 p)
    {
        int cx = Mathf.FloorToInt(p.x / probeCellSize);
        int cy = Mathf.FloorToInt(p.y / probeCellSize);
        int cz = Mathf.FloorToInt(p.z / probeCellSize);
        // Morton-like packing: 20 bits por eixo (range ±524288 m)
        return ((long)(cx & 0xFFFFF)) | ((long)(cy & 0xFFFFF) << 20) | ((long)(cz & 0xFFFFF) << 40);
    }

    private static Vector3 FibonacciSphereDirection(int index, int total)
    {
        const float goldenAngle = Mathf.PI * (3f - 2.3606797f);
        float phi = Mathf.Acos(1f - 2f * (index + 0.5f) / total);
        float theta = goldenAngle * index;
        return new Vector3(
            Mathf.Sin(phi) * Mathf.Cos(theta),
            Mathf.Cos(phi),
            Mathf.Sin(phi) * Mathf.Sin(theta));
    }

    /// <summary>
    /// Mede a espessura real da parede/superfície atravessada, fazendo um
    /// segundo raycast restrito EXCLUSIVAMENTE a esse collider (Collider.Raycast
    /// não testa contra o resto da cena, só contra o próprio collider) —
    /// achando onde o raio sairia do outro lado. Se não encontrar saída
    /// (geometria não-convexa problemática, raio raspando uma borda, etc.),
    /// usa o fallback configurado em vez de travar/retornar 0.
    /// </summary>
    private float MeasureWallThickness(RaycastHit entryHit, Vector3 direction)
    {
        Ray throughRay = new Ray(entryHit.point + direction * 0.001f, direction);

        if (entryHit.collider.Raycast(throughRay, out RaycastHit exitHit, maxWallThicknessProbeDistance))
        {
            return Mathf.Max(0.01f, exitHit.distance + 0.001f);
        }

        return defaultWallThicknessFallback;
    }

    /// <summary>
    /// Wrapper seguro de Collider.ClosestPoint — essa API só suporta
    /// BoxCollider, SphereCollider, CapsuleCollider e MeshCollider CONVEXO;
    /// pra qualquer outro tipo (o caso mais comum sendo MeshCollider
    /// não-convexo, típico de portas/geometria detalhada) ela lança exceção
    /// em runtime. Nesses casos, cai pra ClosestPointOnBounds — menos preciso
    /// (usa a caixa delimitadora, não a superfície real), mas funciona pra
    /// qualquer collider sem nunca lançar.
    /// </summary>
    public static Vector3 SafeClosestPoint(Collider collider, Vector3 point)
    {
        bool supportsClosestPoint =
            collider is BoxCollider ||
            collider is SphereCollider ||
            collider is CapsuleCollider ||
            (collider is MeshCollider meshCollider && meshCollider.convex);

        return supportsClosestPoint
            ? collider.ClosestPoint(point)
            : collider.ClosestPointOnBounds(point);
    }

    private bool HasDirectLineOfSight(
    Vector3 point,
    Vector3 listenerPosition)
    {
        Vector3 toListener = listenerPosition - point;
        float distance = toListener.magnitude;

        if (distance <= 0.001f)
            return true;

        Vector3 direction = toListener / distance;

        /*Debug.DrawLine(
            point + direction * 0.05f,
            direction,
            Color.red,
            1f
        );*/

        return !Physics.Raycast(
            point + direction * 0.05f,
            direction,
            distance - 0.1f,
            wallMask
        );
    }

    private void TestSingleEmitterAtPoint(
    SoundEmitter emitter,
    Vector3 probePoint,
    float traveledSoFar,
    int wallHitsSoFar,
    float muffleSoFar,
    float energySoFar,
    Vector3 listenerPosition,
    Collider transmissionCollider = null,
    Vector3 lastDirectPoint = default,
    bool hasDirectPoint = false,
    int reflectionBouncesSoFar = 0)
    {
        {
            if (emitter == null || !emitter.IsPlaying) return;

            Vector3 emitterPos = emitter.transform.position;
            float dist;
            Vector3 toEmitter;

            if (emitter.SourceType == SoundSourceType.Point)
            {
                toEmitter = emitterPos - probePoint;
                float distSq = toEmitter.sqrMagnitude;

                dist = Mathf.Sqrt(distSq);
            }
            else
            {
                Vector3 rayDir = (probePoint - listenerPosition).normalized;

                toEmitter = rayDir * maxDistance;
                dist = probeCellSize * 2f;
            }

            float totalDist = traveledSoFar + dist;
            float openDistance = traveledSoFar;

            int finalWallHits = wallHitsSoFar;
            float energyScale = 1f;

            Ray obstructionRay = new Ray(probePoint, toEmitter / dist);

            bool isGlobal = emitter.SourceType == SoundSourceType.Global;

            float attenuation = isGlobal ? 1f : 1f / Mathf.Pow(1f + totalDist * distanceFalloff, 2f);
            float maxIntensity = isGlobal ? emitter.loudness : emitter.loudness * energySoFar * attenuation;

            if (maxIntensity <= minAudibleIntensity) return;

            float finalIntensity = isGlobal ? emitter.loudness : maxIntensity;

            if (finalIntensity <= minAudibleIntensity) return;

            int hitCount = Physics.RaycastNonAlloc(obstructionRay, raycastBuffer, dist - 0.05f, wallMask);

            bool blocked = false;
            RaycastHit obstructionHit = default;
            float closestDist = float.MaxValue;

            for (int h = 0; h < hitCount; h++)
            {
                RaycastHit hit = raycastBuffer[h];
                if (hit.collider.GetComponentInParent<SoundEmitter>() == emitter)
                    continue;

                if (hit.collider.GetComponentInParent<SoundListener>() != null)
                    continue;

                if (hit.distance < closestDist)
                {
                    closestDist = hit.distance;
                    obstructionHit = hit;
                    blocked = true;
                }
            }

            float distanceMuffle = emitter.SourceType == SoundSourceType.Point ? 1f - Mathf.Exp(-totalDist * distanceMuffleRate) : 0f;
            float finalMuffle = Mathf.Clamp01(muffleSoFar + distanceMuffle);

            if (blocked)
            {
                SoundMaterial obstructionMaterial = GetCachedMaterial(obstructionHit.collider);

                float blockingTransmission = obstructionMaterial != null ? obstructionMaterial.transmissionCoefficient : 0f;
                float blockingMuffle = obstructionMaterial != null ? obstructionMaterial.transmissionMuffle : defaultTransmissionMuffle;

                float volumeScale = Mathf.Lerp(1f, 0f, occlusionFactor);

                if (blockingTransmission <= 0f)
                    energyScale *= volumeScale;
                else
                    energyScale *= blockingTransmission;

                finalMuffle = Mathf.Min(1f, finalMuffle + blockingMuffle);
                finalWallHits++;

                if (!isGlobal)
                    finalIntensity *= energyScale;

#if UNITY_EDITOR
                if (debugSettings.showObstructionRays)
                {
                    Color c = obstructionMaterial != null ? Color.yellow : Color.red;
                    Debug.DrawLine(probePoint, obstructionHit.point, c, 0f);
                    Debug.DrawLine(obstructionHit.point, emitterPos, c * 0.5f, 0f);
                }
#endif
            }
#if UNITY_EDITOR
            else if (debugSettings.showObstructionRays)
            {
                Debug.DrawLine(probePoint, emitterPos, Color.green, 0f);
            }
#endif

            if (emitter.SourceType == SoundSourceType.Global)
            {
                finalMuffle = Mathf.Clamp01(finalMuffle);
                finalWallHits = wallHitsSoFar;
            }

#if UNITY_EDITOR
            if (debugSettings.showProbeLinks)
                Debug.DrawLine(probePoint, emitterPos, new Color(0f, 1f, 1f, 0.3f), 0f);
#endif
            float openingWeight = finalIntensity * (1f - finalMuffle);
            if (finalIntensity <= minAudibleIntensity) return;

            // ── Direção percebida ─────────────────────────────────────────────
            Vector3 baseDir = (emitterPos - listenerPosition).normalized;
            Vector3 perceivedDir;

            if (emitter.SourceType == SoundSourceType.Global)
            {
                Vector3 directionFromListener = probePoint - listenerPosition;

                perceivedDir = directionFromListener.sqrMagnitude > 0.001f
                    ? directionFromListener.normalized
                    : Vector3.zero;
            }
            else if (finalWallHits > 0)
            {
                if (reflectionBouncesSoFar > 0)
                    finalMuffle = Mathf.Clamp01(finalMuffle + reflectionMuffle);

                if (hasDirectPoint)
                {
                    Vector3 toDirectPoint = lastDirectPoint - listenerPosition;

                    perceivedDir = toDirectPoint.sqrMagnitude > 0.0001f
                        ? toDirectPoint.normalized
                        : emitterPos;
                }
                else
                {
                    perceivedDir = (emitterPos - listenerPosition).normalized;
                }
            }
            else
            {
                // LOS direto, sem nenhuma parede/bounce envolvida — direção
                // exata, sem blend, e finalMuffle fica só com a base de
                // distância (nenhum termo de transmissão/reflexão somado).
                perceivedDir = emitterPos;
            }

            Vector3 perceivedSourcePoint;

            if (emitter.SourceType == SoundSourceType.Global)
            {
                perceivedSourcePoint = probePoint;
            }
            else if (finalWallHits == 0)
            {
                perceivedSourcePoint = emitterPos;
            }
            else if (transmissionCollider != null)
            {
                perceivedSourcePoint = SafeClosestPoint(transmissionCollider, listenerPosition);
            }
            else if (hasDirectPoint)
            {
                perceivedSourcePoint = lastDirectPoint;
            }
            else
            {
                perceivedSourcePoint = emitterPos;
            }

            // ── Acumulação ───────────────────────────────────────────────────
            EntityId id = emitter.GetEntityId();
            frameAccumulated.TryGetValue(id, out AccumulatedPath acc);

            if (acc.globalEntries == null)
                acc.globalEntries = new List<GlobalEntry>(4);

            acc.intensitySum += finalIntensity;

            acc.wallHitsWeightedSum += finalWallHits * finalIntensity;

            acc.emitter = emitter;

            if (emitter.SourceType != SoundSourceType.Global)
            {
                if (acc.directionSamples == null)
                    acc.directionSamples = new List<DirectionSample>(4);

                acc.directionSamples.Add(new DirectionSample
                {
                    direction = perceivedDir,
                    point = perceivedSourcePoint,
                    intensity = finalIntensity,
                    muffle = finalMuffle
                });

                if (acc.directionSamples.Count > 4)
                {
                    int weakest = 0;

                    for (int s = 1; s < acc.directionSamples.Count; s++)
                    {
                        if (acc.directionSamples[s].intensity < acc.directionSamples[weakest].intensity)
                        {
                            weakest = s;
                        }
                    }

                    acc.directionSamples.RemoveAt(weakest);
                }
            }

            if (emitter.SourceType == SoundSourceType.Global)
            {
                GlobalEntry entry = new GlobalEntry
                {
                    point = probePoint,
                    direction = perceivedDir,
                    intensity = finalIntensity,
                    muffle = finalMuffle,
                    wallHits = finalWallHits
                };

                acc.globalEntries.Add(entry);

                if (acc.globalEntries.Count > 4)
                {
                    int weakest = 0;

                    for (int i = 1; i < acc.globalEntries.Count; i++)
                    {
                        if (acc.globalEntries[i].intensity < acc.globalEntries[weakest].intensity)
                        {
                            weakest = i;
                        }
                    }

                    acc.globalEntries.RemoveAt(weakest);
                }
            }

            if (finalIntensity > acc.bestIntensity)
            {
                acc.bestIntensity = finalIntensity;
                acc.bestPerceivedDirection = perceivedDir;
                acc.bestPerceivedPoint = perceivedSourcePoint;
                acc.bestWallHits = finalWallHits;
                acc.bestDistance = totalDist;
                acc.bestMuffle = finalMuffle;
                acc.bestTransmissionCollider = transmissionCollider;
                acc.bestHasReflectionPoint = finalWallHits > 0 && transmissionCollider == null && hasDirectPoint;
            }

            frameAccumulated[id] = acc;
        }
    }

    private void AccumulateFromProbeCache(Vector3 listenerPosition)
    {
        for (int r = 0; r < totalRays; r++)
        {
            var cache = cachedProbesByRay[r];
            for (int p = 0; p < cache.Count; p++)
            {
                ProbeSample s = cache[p];
                TestEmittersFromPoint(
                    s.point, s.traveledSoFar,
                    s.wallHits, s.transmissionMuffle, s.energySoFar,
                    listenerPosition, s.transmissionCollider, s.lastDirectPoint, s.hasDirectPoint,
                    s.reflectionBounces);
            }
        }

        for (int e = 0; e < emitters.Count; e++)
        {
            SoundEmitter emitter = emitters[e];
            if (emitter != null && emitter.NeedsCatchUp)
                emitter.MarkCaughtUp();
        }
    }

    private readonly Dictionary<Collider, SoundMaterial> materialCache = new();

    private SoundMaterial GetCachedMaterial(Collider c)
    {
        if (!materialCache.TryGetValue(c, out var material))
        {
            material = c.GetComponent<SoundMaterial>(); // pode ser null, cacheia null também
            materialCache[c] = material;
        }
        return material;
    }

    private void BuildActiveEmitters(Vector3 listenerPosition)
    {
        activeEmitters.Clear();
        for (int i = 0; i < emitters.Count; i++)
        {
            SoundEmitter e = emitters[i];
            if (e == null || !e.IsPlaying) continue;

            if (e.SourceType == SoundSourceType.Point)
            {
                float d = Vector3.Distance(e.transform.position, listenerPosition);
                float k = 1f + Mathf.Max(0f, d - cullSlack) * distanceFalloff;
                if (e.loudness / (k * k) <= minAudibleIntensity) continue;
            }
            activeEmitters.Add(e);
        }
    }
}