using System;

namespace GameOfLife.Core
{
    /// <summary>
    /// 把构型的局部坐标合成为网格绝对坐标。
    /// <para>
    /// CPU 与 GPU 两条盖印路径都调用这里，目的是让"坐标计算"只有一处实现——
    /// 两条路径各自算一遍，是这类项目最典型的隐性分歧来源。
    /// 越界校验的策略是"抛异常"而不是静默裁剪：盖印位置算错时必须立刻暴露。
    /// </para>
    /// </summary>
    public static class PatternStamp
    {
        /// <summary>合成绝对坐标（局部坐标 + 原点）。不校验，不裁剪。</summary>
        public static GridPos[] ComposeAbsolute(PatternDefinition pattern, int originX, int originY)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));

            var cells = new GridPos[pattern.Cells.Count];
            for (int i = 0; i < cells.Length; i++)
            {
                GridPos local = pattern.Cells[i];
                cells[i] = new GridPos(originX + local.X, originY + local.Y);
            }

            return cells;
        }

        /// <summary>校验合成后的坐标是否全部落在网格内，越界即抛异常。</summary>
        public static void ValidateInside(
            PatternDefinition pattern, int originX, int originY, int width, int height)
        {
            GridPos[] cells = ComposeAbsolute(pattern, originX, originY);
            foreach (GridPos cell in cells)
            {
                if (cell.X < 0 || cell.Y < 0 || cell.X >= width || cell.Y >= height)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(originX),
                        $"{pattern.Name} 在原点 ({originX},{originY}) 盖印后会越界：细胞 {cell} " +
                        $"不在 {width}x{height} 网格内");
                }
            }
        }

        /// <summary>让构型在网格中居中时，包围盒左下角应放置的原点。</summary>
        public static GridPos CenterOrigin(PatternDefinition pattern, int width, int height)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (pattern.Width > width || pattern.Height > height)
            {
                throw new ArgumentException(
                    $"{pattern.Name}（{pattern.Width}x{pattern.Height}）放不进 {width}x{height} 网格");
            }

            return new GridPos((width - pattern.Width) / 2, (height - pattern.Height) / 2);
        }
    }
}
