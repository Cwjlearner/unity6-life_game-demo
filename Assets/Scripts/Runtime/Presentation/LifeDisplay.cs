using UnityEngine;
using UnityEngine.UI;

namespace GameOfLife.Presentation
{
    /// <summary>
    /// 把控制层的状态纹理显示到 uGUI 的 RawImage 上。选 uGUI 而不是自建面片材质，
    /// 是因为 URP 2D 渲染器下 uGUI 的显示路径最稳，且 uvRect 可直接处理 Y 方向差异。
    /// </summary>
    [AddComponentMenu("Game Of Life/Life Display")]
    public sealed class LifeDisplay : MonoBehaviour
    {
        [SerializeField] private LifeController _controller;
        [SerializeField] private RawImage _surface;

        [Tooltip("纹理与屏幕 Y 方向不一致时打开。用不对称图案（如左上角放一个方块）确认一次即可。")]
        [SerializeField] private bool _flipY;

        private static readonly Rect s_Normal = new Rect(0f, 0f, 1f, 1f);
        private static readonly Rect s_Flipped = new Rect(0f, 1f, 1f, -1f);

        public LifeController Controller
        {
            get => _controller;
            set => _controller = value;
        }

        public RawImage Surface
        {
            get => _surface;
            set => _surface = value;
        }

        public bool FlipY
        {
            get => _flipY;
            set => _flipY = value;
        }

        private void LateUpdate()
        {
            if (_controller == null || _surface == null) return;

            Texture texture = _controller.DisplayTexture;
            if (texture != null && _surface.texture != texture)
                _surface.texture = texture;

            Rect wanted = _flipY ? s_Flipped : s_Normal;
            if (_surface.uvRect != wanted)
                _surface.uvRect = wanted;
        }
    }
}
