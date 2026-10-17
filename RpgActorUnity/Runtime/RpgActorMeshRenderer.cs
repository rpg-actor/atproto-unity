using UnityEngine;

namespace RpgActorUnity
{
    [ExecuteAlways, RequireComponent(typeof(MeshRenderer))]
    public class RpgActorMeshRenderer : MonoBehaviour
    {
        private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseMapST = Shader.PropertyToID("_BaseMap_ST");

        public RpgActor Actor;

        private MeshRenderer _renderer;
        private MaterialPropertyBlock _block;
        private Sprite _applied;

        private void Reset() => Actor = GetComponentInParent<RpgActor>();

        private void OnEnable()
        {
            _renderer = GetComponent<MeshRenderer>();
            _block = new MaterialPropertyBlock();
        }

        private void OnDisable()
        {
            _renderer.SetPropertyBlock(null);
            _applied = null;
        }

        private void LateUpdate()
        {
            var sprite = Actor ? Actor.CurrentSprite : null;
            if (ReferenceEquals(sprite, _applied)) return;
            _applied = sprite;

            if (!sprite || !sprite.texture)
            {
                _renderer.SetPropertyBlock(null);
                return;
            }

            var texture = sprite.texture;
            var rect = sprite.rect;
            _block.SetTexture(BaseMap, texture);
            _block.SetVector(BaseMapST, new Vector4(
                rect.width / texture.width, rect.height / texture.height,
                rect.x / texture.width, rect.y / texture.height));
            _renderer.SetPropertyBlock(_block);
        }
    }
}
