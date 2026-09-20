using UnityEngine;
using UnityUtility.Audio;
using System.Collections;

public class SoundEmitter : MonoBehaviour
{
    public SoundData data { get; private set; }

    private AudioSource audioSource;
    private Coroutine coroutine;
    private bool isReleased = true;

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        if (!audioSource)
            audioSource = gameObject.AddComponent<AudioSource>();
    }

    public void Play()
    {
        if (coroutine != null)
            StopCoroutine(coroutine);

        audioSource.Play();
        coroutine = StartCoroutine(WaitForSoundToEnd());
    }

    public void Stop()
    {
        if (coroutine != null)
        {
            StopCoroutine(coroutine);
            coroutine = null;
        }

        audioSource.Stop();
        Release();
    }

    IEnumerator WaitForSoundToEnd()
    {
        yield return new WaitWhile(() => audioSource.isPlaying);
        coroutine = null;
        Release();
    }

    private void Release()
    {
        if (isReleased) return;
        isReleased = true;
        SoundManager.Instance.ReturnToPool(this);
    }

    public void Initialize(SoundData newData)
    {
        isReleased = false;

        data = newData;
        audioSource.clip = newData.clip;
        audioSource.loop = newData.loop;
        audioSource.pitch = newData.pitch;
        audioSource.volume = newData.volume;
        audioSource.playOnAwake = newData.playOnAwake;
        audioSource.outputAudioMixerGroup = newData.group;
    }

    public void WithRandomPitch(float min = -0.05f, float max = 0.05f)
    {
        audioSource.pitch += Random.Range(min, max);
    }
}