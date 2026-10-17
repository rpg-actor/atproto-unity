using RpgActorUnity;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class Walker25D : MonoBehaviour
{
    public RpgActor Actor;
    public Vector2 Move;
    public float Speed = 3.5f;
    public float FrameDurationAtFullSpeed = 0.12f;

    private CharacterController _controller;
    private float _fallSpeed;

    private void Awake() => _controller = GetComponent<CharacterController>();

    private void Update()
    {
        _controller.enabled = Actor.Status == RpgActorStatus.Ready;
        if (!_controller.enabled) return;

        var camera = Camera.main.transform;
        var forward = Vector3.ProjectOnPlane(camera.forward, Vector3.up).normalized;
        var right = Vector3.ProjectOnPlane(camera.right, Vector3.up).normalized;
        var move = Vector2.ClampMagnitude(Move, 1);

        _fallSpeed = _controller.isGrounded ? -1 : _fallSpeed + Physics.gravity.y * Time.deltaTime;
        var velocity = (right * move.x + forward * move.y) * Speed + Vector3.up * _fallSpeed;
        _controller.Move(velocity * Time.deltaTime);

        var groundSpeed = Vector3.ProjectOnPlane(_controller.velocity, Vector3.up).magnitude / Speed;
        Actor.IsWalking = groundSpeed > 0.1f;
        if (!Actor.IsWalking) return;

        Actor.FrameDuration = FrameDurationAtFullSpeed / Mathf.Max(groundSpeed, 0.3f);
        Actor.Direction = Mathf.Abs(move.x) > Mathf.Abs(move.y)
            ? move.x > 0 ? RpgActorDirection.Right : RpgActorDirection.Left
            : move.y > 0 ? RpgActorDirection.Up : RpgActorDirection.Down;
    }
}
