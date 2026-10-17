using RpgActorUnity;
using TMPro;
using UnityEngine;

public class ActorLabel : MonoBehaviour
{
    public RpgActor Actor;
    public TMP_Text Label;

    private void LateUpdate()
    {
        var text = Actor.Status == RpgActorStatus.Ready ? Actor.Handle : "";
        if (Label.text != text)
            Label.text = text;
    }
}
