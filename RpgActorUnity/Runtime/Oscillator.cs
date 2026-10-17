using UnityEngine;

namespace RpgActorUnity
{
    public class Oscillator : MonoBehaviour
    {
        public Vector3 Amplitude = new(0, 1, 0);
        public float Frequency = 1;

        private Vector3 _origin;

        private void Start() => _origin = transform.localPosition;

        private void Update() =>
            transform.localPosition = _origin + Amplitude * Mathf.Sin(2 * Mathf.PI * Frequency * Time.time);
    }
}
