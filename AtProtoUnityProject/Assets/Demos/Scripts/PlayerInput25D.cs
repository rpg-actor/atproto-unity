using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerInput25D : MonoBehaviour
{
    public Walker25D Walker;
    public HandlePrompt Prompt;

    private InputAction _move;

    private void Awake() => _move = InputSystem.actions.FindAction("Player/Move");

    private void Update() => Walker.Move = Prompt.Done ? _move.ReadValue<Vector2>() : Vector2.zero;
}
