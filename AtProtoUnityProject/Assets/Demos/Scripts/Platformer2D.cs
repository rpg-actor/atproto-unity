using RpgActorUnity;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody2D))]
public class Platformer2D : MonoBehaviour
{
    public RpgActor Actor;
    public HandlePrompt Prompt;
    public float Speed = 5;
    public float Acceleration = 40;
    public float JumpSpeed = 12;
    public float CoyoteTime = 0.1f;
    public float JumpBufferTime = 0.12f;
    public float FrameDurationAtFullSpeed = 0.1f;

    private Rigidbody2D _body;
    private InputAction _move;
    private InputAction _jump;
    private ContactFilter2D _ground;
    private float _lastGroundedTime = float.NegativeInfinity;
    private float _lastJumpPressTime = float.NegativeInfinity;
    private bool _wasDone;

    private void Awake()
    {
        _body = GetComponent<Rigidbody2D>();
        _move = InputSystem.actions.FindAction("Player/Move");
        _jump = InputSystem.actions.FindAction("Player/Jump");
        _ground.SetNormalAngle(45, 135);
    }

    private void Update()
    {
        var playing = Prompt.Done && _wasDone;
        _wasDone = Prompt.Done;

        var input = playing ? _move.ReadValue<Vector2>().x : 0;
        if (playing && _jump.WasPressedThisFrame())
            _lastJumpPressTime = Time.time;

        var velocity = _body.linearVelocity;
        var grounded = velocity.y < 0.01f && _body.IsTouching(_ground);
        if (grounded)
            _lastGroundedTime = Time.time;

        velocity.x = Mathf.MoveTowards(velocity.x, input * Speed, Acceleration * Time.deltaTime);
        if (Time.time - _lastJumpPressTime < JumpBufferTime && Time.time - _lastGroundedTime < CoyoteTime)
        {
            velocity.y = _jump.IsPressed() ? JumpSpeed : JumpSpeed * 0.5f;
            _lastJumpPressTime = _lastGroundedTime = float.NegativeInfinity;
        }
        if (_jump.WasReleasedThisFrame() && velocity.y > 0)
            velocity.y *= 0.5f;
        _body.linearVelocity = velocity;

        if (Mathf.Abs(input) > 0.01f)
            Actor.Direction = input > 0 ? RpgActorDirection.Right : RpgActorDirection.Left;
        var groundSpeed = Mathf.Abs(velocity.x) / Speed;
        Actor.IsWalking = grounded && groundSpeed > 0.1f;
        if (Actor.IsWalking)
            Actor.FrameDuration = FrameDurationAtFullSpeed / Mathf.Max(groundSpeed, 0.3f);
    }
}
