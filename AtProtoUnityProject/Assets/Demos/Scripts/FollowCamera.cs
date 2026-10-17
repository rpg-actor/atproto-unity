using UnityEngine;

public class FollowCamera : MonoBehaviour
{
    public Transform Target;
    public float Sharpness = 5;

    private Vector3 _offset;

    private void Start() => _offset = transform.position - Target.position;

    private void LateUpdate()
    {
        var blend = 1 - Mathf.Exp(-Sharpness * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, Target.position + _offset, blend);
    }
}
