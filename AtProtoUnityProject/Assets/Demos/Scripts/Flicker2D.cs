using UnityEngine;
using UnityEngine.Rendering.Universal;

[RequireComponent(typeof(Light2D))]
public class Flicker2D : MonoBehaviour
{
    public float Amount = 0.15f;
    public float Speed = 3;

    private Light2D _light;
    private float _baseIntensity;
    private float _seed;

    private void Awake()
    {
        _light = GetComponent<Light2D>();
        _baseIntensity = _light.intensity;
        _seed = Random.value * 100;
    }

    private void Update() =>
        _light.intensity = _baseIntensity * (1 + Amount * (Mathf.PerlinNoise(_seed, Time.time * Speed) * 2 - 1));
}
