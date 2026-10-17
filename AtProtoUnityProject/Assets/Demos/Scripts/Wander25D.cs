using UnityEngine;

public class Wander25D : MonoBehaviour
{
    public Walker25D Walker;
    public float Radius = 4;
    public float Pace = 0.5f;
    public float MinPause = 1;
    public float MaxPause = 4;
    public float GiveUpAfter = 5;

    private Vector3 _home;
    private Vector3 _target;
    private float _pauseUntil;
    private float _giveUpAt;

    private void Start()
    {
        _home = transform.position;
        Rest();
    }

    private void Update()
    {
        Walker.Move = Vector2.zero;
        if (Time.time < _pauseUntil) return;

        var toTarget = Vector3.ProjectOnPlane(_target - transform.position, Vector3.up);
        if (toTarget.magnitude < 0.2f || Time.time > _giveUpAt)
        {
            Rest();
            return;
        }

        var camera = Camera.main.transform;
        var forward = Vector3.ProjectOnPlane(camera.forward, Vector3.up).normalized;
        var right = Vector3.ProjectOnPlane(camera.right, Vector3.up).normalized;
        var direction = toTarget.normalized;
        Walker.Move = new Vector2(Vector3.Dot(direction, right), Vector3.Dot(direction, forward)) * Pace;
    }

    private void Rest()
    {
        var offset = Random.insideUnitCircle * Radius;
        _target = _home + new Vector3(offset.x, 0, offset.y);
        _pauseUntil = Time.time + Random.Range(MinPause, MaxPause);
        _giveUpAt = _pauseUntil + GiveUpAfter;
    }
}
