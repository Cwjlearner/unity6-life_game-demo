using System;
using System.Collections.Generic;

namespace GameOfLife.Core
{
    /// <summary>
    /// 一个预制构型的纯数据描述。坐标以构型自身包围盒左下角为原点，y 向上。
    /// <para>
    /// <see cref="Period"/> 与 <see cref="Displacement"/> 不是"设定"，而是可验证的断言目标：
    /// 它们由 tools/verify_patterns.py 独立模拟得出，并由 Edit Mode 测试在 C# 实现上复验。
    /// 这两个数字一旦与实现不符，测试会直接红。
    /// </para>
    /// </summary>
    public sealed class PatternDefinition
    {
        public string Name { get; }

        /// <summary>中文显示名（界面用）。未指定时回退为 <see cref="Name"/>。</summary>
        public string DisplayName { get; }

        public PatternCategory Category { get; }

        /// <summary>回到自身（含位移）所需的最小代数。</summary>
        public int Period { get; }

        /// <summary>每个 <see cref="Period"/> 之后整体平移的向量；静物与振荡器为 (0,0)。</summary>
        public GridPos Displacement { get; }

        public IReadOnlyList<GridPos> Cells { get; }

        /// <summary>包围盒宽度（细胞数）。</summary>
        public int Width { get; }

        /// <summary>包围盒高度（细胞数）。</summary>
        public int Height { get; }

        public PatternDefinition(
            string name,
            PatternCategory category,
            int period,
            GridPos displacement,
            string displayName = null,
            params GridPos[] cells)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("构型名不能为空", nameof(name));
            if (cells == null || cells.Length == 0)
                throw new ArgumentException($"{name}: 构型至少需要一个活细胞", nameof(cells));
            if (period < 1)
                throw new ArgumentOutOfRangeException(nameof(period), $"{name}: 周期必须 >= 1");
            if (category == PatternCategory.StillLife && period != 1)
                throw new ArgumentException($"{name}: 静物的周期必须是 1");
            if (category == PatternCategory.Spaceship && displacement.X == 0 && displacement.Y == 0)
                throw new ArgumentException($"{name}: 飞船每个周期必须有非零位移");
            if (category != PatternCategory.Spaceship && (displacement.X != 0 || displacement.Y != 0))
                throw new ArgumentException($"{name}: 只有飞船可以有位移");

            Name = name;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName;
            Category = category;
            Period = period;
            Displacement = displacement;
            Cells = cells;

            int maxX = 0, maxY = 0;
            foreach (GridPos cell in cells)
            {
                if (cell.X < 0 || cell.Y < 0)
                    throw new ArgumentException($"{name}: 细胞坐标 {cell} 必须非负（包围盒左下角为原点）");
                if (cell.X > maxX) maxX = cell.X;
                if (cell.Y > maxY) maxY = cell.Y;
            }

            Width = maxX + 1;
            Height = maxY + 1;
        }

        public override string ToString() =>
            $"{Name} [{Category}] {Width}x{Height} cells={Cells.Count} period={Period} disp={Displacement}";
    }
}
