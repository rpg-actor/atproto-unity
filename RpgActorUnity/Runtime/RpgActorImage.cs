using UnityEngine;
using UnityEngine.UI;

namespace RpgActorUnity
{
    [ExecuteAlways, RequireComponent(typeof(Image))]
    public class RpgActorImage : MonoBehaviour
    {
        public RpgActor Actor;

        private Image _image;

        private void Reset() => Actor = GetComponentInParent<RpgActor>();

        private void OnEnable() => _image = GetComponent<Image>();

        private void LateUpdate()
        {
            var sprite = Actor ? Actor.CurrentSprite : null;
            if (_image.sprite != sprite)
                _image.sprite = sprite;

            var alpha = sprite ? 1 : 0;
            if (_image.canvasRenderer.GetAlpha() != alpha)
                _image.canvasRenderer.SetAlpha(alpha);
        }
    }
}
