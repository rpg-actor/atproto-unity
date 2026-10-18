using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AtProtoUnity;
using UnityEngine;

[assembly: InternalsVisibleTo("RpgActorUnity.Editor")]

namespace RpgActorUnity
{
    public enum RpgActorSource { Server, RpgActor, Texture }
    public enum RpgActorDirection { Down, Left, Right, Up }
    public enum RpgActorStatus { Idle, Loading, Ready, Failed }

    [ExecuteAlways]
    public class RpgActor : MonoBehaviour
    {
        public string Handle;
        public RpgActorSource Source;
        public Texture2D Sheet;
        public RpgActorDirection Direction;
        public bool IsWalking;
        public float FrameDuration = 0.15f;

        public RpgActorStatus Status { get; private set; }
        public string Error { get; private set; }
        public string Did { get; private set; }
        public int Columns { get; private set; }
        public int Rows { get; private set; }

        public Sprite CurrentSprite => _frames?[Row * Columns + Column];

        internal static readonly HashSet<RpgActor> Enabled = new();
        internal bool NeedsUpdate => Request != _loaded || Status == RpgActorStatus.Loading || IsWalking;

        private (string Handle, RpgActorSource Source, Texture2D Sheet) Request => (Handle, Source, Sheet);
        private (string Handle, RpgActorSource Source, Texture2D Sheet) _loaded;
        [NonSerialized] private int _version;
        [NonSerialized] private Texture2D _texture;
        [NonSerialized] private bool _ownsTexture;
        [NonSerialized] private Sprite[] _frames;

        private int Row => Mathf.Min((int)Direction, Rows - 1);
        private double Clock => Application.isPlaying ? Time.timeAsDouble : Time.realtimeSinceStartupAsDouble;

        private int Column
        {
            get
            {
                if (!IsWalking || Columns < 2) return Columns / 2;
                var cycle = 2 * (Columns - 1);
                var step = (int)(Clock / Mathf.Max(FrameDuration, 0.01f) % cycle);
                return step < Columns ? step : cycle - step;
            }
        }

        public async Awaitable Ready()
        {
            while (isActiveAndEnabled && (Request != _loaded || Status == RpgActorStatus.Loading))
                await Awaitable.NextFrameAsync(destroyCancellationToken);
            if (!isActiveAndEnabled)
                throw new InvalidOperationException($"{name} is disabled.");
            if (Status == RpgActorStatus.Failed)
                throw new Exception(Error);
        }

        public void Reload() => _loaded = default;

        public static Texture2D LoadTexture(byte[] png)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(png))
            {
                DestroyImmediate(texture);
                throw new Exception("The sheet is not a valid image.");
            }
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.hideFlags = HideFlags.DontSave;
            return texture;
        }

        private void OnEnable() => Enabled.Add(this);

        private void OnDisable()
        {
            Enabled.Remove(this);
            _version++;
            _loaded = default;
            Unload();
            Status = RpgActorStatus.Idle;
            Did = null;
            Error = null;
        }

        private void Update()
        {
            if (Request != _loaded || (_frames != null && !_texture))
                _ = Load();
        }

        private async Awaitable Load()
        {
            var version = ++_version;
            var (handle, source, sheet) = _loaded = Request;
            var keepTexture = _ownsTexture && sheet && sheet == _texture;
            Unload(keepTexture);
            Did = null;
            Error = null;

            if (source == RpgActorSource.Texture ? !sheet : string.IsNullOrWhiteSpace(handle))
            {
                Status = RpgActorStatus.Idle;
                return;
            }

            Status = RpgActorStatus.Loading;
            try
            {
                if (source == RpgActorSource.Texture)
                {
                    Slice(sheet, keepTexture, 3, 4);
                }
                else
                {
                    var download = await Download(handle, source);
                    if (!this || version != _version)
                    {
                        DestroyImmediate(download.Texture);
                        return;
                    }
                    Did = download.Did;
                    Slice(download.Texture, true, download.Columns, download.Rows);
                }
                Status = RpgActorStatus.Ready;
            }
            catch (Exception e)
            {
                if (!this || version != _version) return;
                Status = RpgActorStatus.Failed;
                Error = e.Message;
            }
        }

        private static async Awaitable<(string Did, Texture2D Texture, int Columns, int Rows)> Download(string handle, RpgActorSource source)
        {
            var identity = await RpgActorApi.ResolveHandle(handle);
            var did = identity.Did;
            if (source == RpgActorSource.RpgActor)
                return (did, LoadTexture(await RpgActorApi.GetNormalizedSheet(did)), 3, 4);

            var record = await RpgActorApi.Require(
                RpgActorApi.Client.GetRecordAsync<SpriteRecord>(identity, "actor.rpg.sprite", "self"));
            if (record?.SpriteSheet?.Ref == null)
                throw new Exception($"{handle} has no sprite sheet.");
            var blob = await RpgActorApi.Require(
                RpgActorApi.Client.GetBlobBytesAsync(identity, record.SpriteSheet.Ref.Cid));
            return (did, LoadTexture(blob), record.Columns, record.Rows);
        }

        private void Slice(Texture2D texture, bool ownsTexture, int columns, int rows)
        {
            if (columns < 1 || rows < 1 || texture.width < columns || texture.height < rows)
            {
                if (ownsTexture) DestroyImmediate(texture);
                throw new Exception($"A {texture.width}x{texture.height} sheet can't be sliced into {columns}x{rows} frames.");
            }

            _texture = texture;
            _ownsTexture = ownsTexture;
            Columns = columns;
            Rows = rows;
            _frames = new Sprite[columns * rows];

            var width = texture.width / columns;
            var height = texture.height / rows;
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var rect = new Rect(column * width, texture.height - (row + 1) * height, width, height);
                var sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0), width, 0, SpriteMeshType.FullRect);
                sprite.hideFlags = HideFlags.DontSave;
                _frames[row * columns + column] = sprite;
            }
        }

        private void Unload(bool keepTexture = false)
        {
            if (_frames != null)
                foreach (var sprite in _frames)
                    DestroyImmediate(sprite);
            if (_ownsTexture && !keepTexture)
                DestroyImmediate(_texture);

            _frames = null;
            _texture = null;
            _ownsTexture = false;
            Columns = 0;
            Rows = 0;
        }

        private class SpriteRecord
        {
            public AtProtoBlob SpriteSheet;
            public int Columns = 3;
            public int Rows = 4;
        }
    }
}
