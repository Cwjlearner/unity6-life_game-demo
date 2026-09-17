using System;
using System.Text;

namespace GameOfLife.Core
{
    /// <summary>
    /// 网格状态的只读快照，位打包存储（每个 ulong 存 64 个细胞，行主序，低位在前）。
    /// <para>
    /// 它是 CPU 实现与 GPU 实现之间的"共同货币"：GPU 侧通过 AsyncGPUReadback 拿到数据后，
    /// 用 <see cref="FromPackedBits"/> 构造同一个类型，于是两条路径可以用完全相同的断言与哈希比对。
    /// </para>
    /// </summary>
    public sealed class GridSnapshot
    {
        private readonly ulong[] _bits;

        public int Width { get; }
        public int Height { get; }
        public int Population { get; }
        public int Generation { get; }

        /// <summary>每行占用的 ulong 数量。</summary>
        public int WordsPerRow { get; }

        private GridSnapshot(int width, int height, int population, int generation)
        {
            Width = width;
            Height = height;
            Population = population;
            Generation = generation;
            WordsPerRow = (width + 63) / 64;
            _bits = new ulong[WordsPerRow * height];
        }

        /// <summary>从布尔网格构造（CPU 路径）。</summary>
        public static GridSnapshot FromCells(int width, int height, bool[] cells, int population, int generation)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            if (cells.Length < width * height)
                throw new ArgumentException($"cells 长度 {cells.Length} 小于 {width}x{height}", nameof(cells));

            var snapshot = new GridSnapshot(width, height, population, generation);
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                int word = y * snapshot.WordsPerRow;
                for (int x = 0; x < width; x++)
                {
                    if (cells[row + x])
                        snapshot._bits[word + (x >> 6)] |= 1UL << (x & 63);
                }
            }

            return snapshot;
        }

        /// <summary>从 GPU 回读的 32 位打包数据构造（GPU 路径）。</summary>
        public static GridSnapshot FromPackedBits(int width, int height, uint[] packed, int population, int generation)
        {
            if (packed == null) throw new ArgumentNullException(nameof(packed));

            var snapshot = new GridSnapshot(width, height, population, generation);
            int wordsPerRowGpu = (width + 31) / 32;
            for (int y = 0; y < height; y++)
            {
                int srcRow = y * wordsPerRowGpu;
                int dstRow = y * snapshot.WordsPerRow;
                for (int x = 0; x < width; x++)
                {
                    int src = srcRow + (x >> 5);
                    if (src >= packed.Length) break;
                    if ((packed[src] & (1u << (x & 31))) != 0)
                        snapshot._bits[dstRow + (x >> 6)] |= 1UL << (x & 63);
                }
            }

            return snapshot;
        }

        public bool Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
            return (_bits[y * WordsPerRow + (x >> 6)] & (1UL << (x & 63))) != 0;
        }

        /// <summary>
        /// 状态哈希（FNV-1a，仅覆盖细胞位，不含代数）。
        /// 用于"周期 n 后是否回到原状态"的判定。
        /// </summary>
        public ulong ComputeHash()
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong hash = offset;
            for (int i = 0; i < _bits.Length; i++)
            {
                ulong word = _bits[i];
                for (int b = 0; b < 8; b++)
                {
                    hash ^= word & 0xFFUL;
                    hash *= prime;
                    word >>= 8;
                }
            }

            return hash;
        }

        /// <summary>调试与测试失败时输出可读图案，首行对应 y = Height-1。</summary>
        public string ToAscii()
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(Width).Append('x').Append(Height)
              .Append(" population=").Append(Population)
              .Append(" generation=").Append(Generation).AppendLine();

            for (int y = Height - 1; y >= 0; y--)
            {
                for (int x = 0; x < Width; x++)
                    sb.Append(Get(x, y) ? '#' : '.');
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>裁剪到包围盒的紧凑 ASCII，便于断言信息与文档插图。</summary>
        public string ToTrimmedAscii()
        {
            int minX = Width, minY = Height, maxX = -1, maxY = -1;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (!Get(x, y)) continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0) return "<empty>";

            var sb = new StringBuilder();
            sb.Append("# bbox=(").Append(minX).Append(',').Append(minY)
              .Append(")..(").Append(maxX).Append(',').Append(maxY)
              .Append(") population=").Append(Population).AppendLine();

            for (int y = maxY; y >= minY; y--)
            {
                for (int x = minX; x <= maxX; x++)
                    sb.Append(Get(x, y) ? '#' : '.');
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
