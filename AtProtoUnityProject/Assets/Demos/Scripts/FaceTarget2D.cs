using RpgActorUnity;
using UnityEngine;

public class FaceTarget2D : MonoBehaviour
{
    public RpgActor Actor;
    public Transform Target;
    public float FaceDownDistance = 4;

    private void Update()
    {
        var offset = Target.position.x - transform.position.x;
        Actor.Direction = Mathf.Abs(offset) > FaceDownDistance ? RpgActorDirection.Down
            : offset > 0 ? RpgActorDirection.Right : RpgActorDirection.Left;
    }
}
