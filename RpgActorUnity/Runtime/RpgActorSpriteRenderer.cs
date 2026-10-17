using UnityEngine;

namespace RpgActorUnity
{
    [ExecuteAlways, RequireComponent(typeof(SpriteRenderer))]
    public class RpgActorSpriteRenderer : MonoBehaviour
    {
        public RpgActor Actor;

        private SpriteRenderer _renderer;

        private void Reset() => Actor = GetComponentInParent<RpgActor>();

        private void OnEnable() => _renderer = GetComponent<SpriteRenderer>();

        private void LateUpdate()
        {
            var sprite = Actor ? Actor.CurrentSprite : null;
            if (_renderer.sprite != sprite)
                _renderer.sprite = sprite;
        }
    }
}
