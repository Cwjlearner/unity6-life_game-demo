using System;

namespace GameOfLife.Core
{
    /// <summary>
    /// CPU 参考实现。刻意保持朴素：逐格统计 8 邻域 + 整格双缓冲。
    /// <para>
    /// 它的价值不是性能，而是"可读的正确性基线"：规则一目了然、可断点、不依赖 GPU。
    /// GPU 实现的每一档优化都拿它做交叉验证。
    /// </para>
    /// </summary>
    public sealed class GridModel : IGridKernel
    {
        private readonly bool[] _current;
        private readonly bool[] _next;

        public int Width { get; }
        public int Height { get; }
        public bool WrapEdges { get; }
        public int Generation { get; private set; }
        public int Population { get; private set; }

        public GridModel(int width, int height, bool wrapEdges = true)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "宽度必须为正");
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "高度必须为正");

            Width = width;
            Height = height;
            WrapEdges = wrapEdges;
            _current = new bool[width * height];
            _next = new bool[width * height];
        }

        /// <summary>读取细胞。越界时：环面模式回绕，死边界模式返回 false。</summary>
        public bool GetCell(int x, int y)
        {
            if (WrapEdges)
            {
                x = Wrap(x, Width);
                y = Wrap(y, Height);
                return _current[y * Width + x];
            }

            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
            return _current[y * Width + x];
        }

        /// <summary>写入细胞。越界抛异常——盖印前应保证位置合法，静默裁剪会掩盖 bug。</summary>
        public void SetCell(int x, int y, bool alive)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height)
                throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y}) 超出 {Width}x{Height} 网格");

            int index = y * Width + x;
            if (_current[index] == alive) return;

            _current[index] = alive;
            Population += alive ? 1 : -1;
        }

        public void Clear()
        {
            Array.Clear(_current, 0, _current.Length);
            Array.Clear(_next, 0, _next.Length);
            Population = 0;
            Generation = 0;
        }

        /// <summary>
        /// 按种子填充随机初始状态。与 GPU 路径共用 <see cref="RandomField"/> 的整数哈希，
        /// 因此同一个种子在两条路径上会得到完全相同的棋盘。
        /// </summary>
        public void FillRandom(int seed, float aliveProbability = 0.32f)
        {
            uint threshold = RandomField.Threshold(aliveProbability);
            uint hashSeed = unchecked((uint)seed);

            Array.Clear(_next, 0, _next.Length);

            int population = 0;
            for (int y = 0; y < Height; y++)
            {
                int row = y * Width;
                for (int x = 0; x < Width; x++)
                {
                    bool alive = RandomField.IsAlive(hashSeed, x, y, threshold);
                    _current[row + x] = alive;
                    if (alive) population++;
                }
            }

            Population = population;
            Generation = 0;
        }

        public void StampPattern(PatternDefinition pattern, int originX, int originY)
        {
            // 坐标合成与 GPU 路径共用同一实现，越界由 SetCell 抛出。
            GridPos[] cells = PatternStamp.ComposeAbsolute(pattern, originX, originY);
            for (int i = 0; i < cells.Length; i++)
                SetCell(cells[i].X, cells[i].Y, true);
        }

        /// <summary>统计 8 邻域活细胞数（不含自身）。</summary>
        public int CountNeighbours(int x, int y)
        {
            int count = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    if (GetCell(x + dx, y + dy)) count++;
                }
            }

            return count;
        }

        public void Step()
        {
            int population = 0;

            for (int y = 0; y < Height; y++)
            {
                int row = y * Width;
                for (int x = 0; x < Width; x++)
                {
                    int neighbours = CountNeighbours(x, y);
                    bool alive = _current[row + x];
                    bool next = alive
                        ? neighbours == 2 || neighbours == 3   // 生存：S23
                        : neighbours == 3;                     // 诞生：B3
                    _next[row + x] = next;
                    if (next) population++;
                }
            }

            Array.Copy(_next, _current, _current.Length);
            Population = population;
            Generation++;
        }

        public GridSnapshot Snapshot() =>
            GridSnapshot.FromCells(Width, Height, _current, Population, Generation);

        /// <summary>用快照覆盖当前状态（测试、回放、GPU→CPU 同步用）。</summary>
        public void LoadFrom(GridSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.Width != Width || snapshot.Height != Height)
                throw new ArgumentException(
                    $"快照尺寸 {snapshot.Width}x{snapshot.Height} 与网格 {Width}x{Height} 不一致", nameof(snapshot));

            int population = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    bool alive = snapshot.Get(x, y);
                    _current[y * Width + x] = alive;
                    if (alive) population++;
                }
            }

            Population = population;
            Generation = snapshot.Generation;
        }

        private static int Wrap(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }
}
