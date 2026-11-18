using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Aplica no AudioMixer o reverb calculado pelos raios do LISTENER
/// (SoundWorld.Reverb). Existe um único reverb no jogo — o do ambiente ao
/// redor de quem ouve — então este é o único lugar que escreve nos parâmetros
/// de reverb do mixer. SoundEmitter não mexe mais nisso.
/// Pode ficar em qualquer GameObject da cena (ex.: junto do SoundListener).
/// </summary>
public class ListenerReverb : MonoBehaviour
{
    [Header("Mixer")]
    [SerializeField] private AudioMixer mixer;

    [Tooltip("Parâmetro exposto (dB) do send/volume do reverb. Vazio = não controla.")]
    [SerializeField] private string wetParamName = "Reverb";

    [Tooltip("Parâmetro exposto (segundos) do Decay Time do efeito de reverb. Vazio = não controla.")]
    [SerializeField] private string decayParamName = "";

    [Header("Mapeamento")]
    [Tooltip("dB quando não há reverb (wet = 0).")]
    [SerializeField] private float minWetDb = -80f;

    [Tooltip("dB com reverb máximo (wet = 1).")]
    [SerializeField] private float maxWetDb = -10f;

    [Tooltip("Expoente aplicado ao wet antes de virar dB. 1 = linear em dB (comportamento antigo). Menor que 1 faz o reverb aparecer mais cedo em ambientes só parcialmente fechados.")]
    [SerializeField] private float wetResponsePower = 1f;

    [Header("Suavização")]
    [Tooltip("Velocidade de suavização do wet (maior = mais rápido).")]
    [SerializeField] private float wetSmoothing = 3f;

    [Tooltip("Velocidade de suavização do decay time.")]
    [SerializeField] private float decaySmoothing = 2f;

    private float currentWet;
    private float currentDecay = 0.2f;
    private bool initialized;

    private void Update()
    {
        if (mixer == null || SoundWorld.Instance == null) return;

        SoundWorld.ReverbData target = SoundWorld.Instance.Reverb;

        if (!initialized)
        {
            currentWet = target.wetLevel;
            currentDecay = target.decayTime;
            initialized = true;
        }
        else
        {
            currentWet = Mathf.Lerp(currentWet, target.wetLevel, 1f - Mathf.Exp(-wetSmoothing * Time.deltaTime));
            currentDecay = Mathf.Lerp(currentDecay, target.decayTime, 1f - Mathf.Exp(-decaySmoothing * Time.deltaTime));
        }

        if (!string.IsNullOrEmpty(wetParamName))
        {
            float db = Mathf.Lerp(minWetDb, maxWetDb, Mathf.Pow(Mathf.Clamp01(currentWet), wetResponsePower));
            mixer.SetFloat(wetParamName, db);
        }

        if (!string.IsNullOrEmpty(decayParamName))
            mixer.SetFloat(decayParamName, currentDecay);
    }
}