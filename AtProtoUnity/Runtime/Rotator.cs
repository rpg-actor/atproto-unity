using UnityEngine;

namespace AtProtoUnity
{
    public class Rotator : MonoBehaviour
    {
        public Vector3 DegreesPerSecond = new(0, 90, 0);

        private void Update() => transform.Rotate(DegreesPerSecond * Time.deltaTime);
    }
}
