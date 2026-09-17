namespace GameOfLife.Core
{
    /// <summary>
    /// 网格坐标。约定：x 向右为正，y 向上为正（与 Unity 2D 世界坐标一致）。
    /// 注意与纹理行号（自上而下）的差异，显示层需要翻转一次，详见实现文档"已知坑"。
    /// </summary>
    public readonly struct GridPos
    {
        public readonly int X;
        public readonly int Y;

        public GridPos(int x, int y)
        {
            X = x;
            Y = y;
        }

        public override string ToString() => $"({X},{Y})";
    }
}
