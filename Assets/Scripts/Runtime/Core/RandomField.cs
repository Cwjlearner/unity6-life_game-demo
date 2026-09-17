using System;

namespace GameOfLife.Core
{
    /// <summary>
    /// 确定性随机初始状态：细胞是否存活完全由 (seed, x, y) 决定，不依赖任何随机数生成器状态。
    /// <para>
    /// 这样做有两个好处：
    /// 1. **可复现**：同一个种子在任何机器、任何顺序下都得到同一张棋盘；
    /// 2. **CPU/GPU 必然一致**：compute shader 里有完全相同的一份整数哈希实现，
    ///    两条路径各自算自己的，结果必须逐位相同——这条由 GPU 一致性测试把关。
    /// </para>
    /// <para>
    /// 哈希只用 uint 的乘法、异或、右移，都是精确定义的定点运算，不存在浮点误差问题。
    /// 阈值也比较整数，避免"浮点除法在两边舍入不同"导致的偶发不一致。
    /// </para>
    /// </summary>
    public static class RandomField
    {
        /// <summary>与 LifePingPong.compute 中的 RandomHash 必须逐行一致。</summary>
        public static uint Hash(uint seed, uint x, uint y)
        {
            unchecked
            {
                uint h = seed * 0x9E3779B9u;
                h ^= x * 0x85EBCA6Bu;
                h = (h ^ (h >> 13)) * 0xC2B2AE35u;
                h ^= y * 0x27D4EB2Fu;
                h = (h ^ (h >> 15)) * 0x165667B1u;
                h ^= h >> 13;
                return h;
            }
        }

        /// <summary>把存活概率换算成整数阈值：哈希值小于阈值的格子为活细胞。</summary>
        public static uint Threshold(float aliveProbability)
        {
            double p = aliveProbability;
            if (p < 0.0) p = 0.0;
            if (p > 1.0) p = 1.0;

            return (uint)(p * 4294967295.0);
        }

        public static bool IsAlive(uint seed, int x, int y, uint threshold) =>
            Hash(seed, (uint)x, (uint)y) < threshold;

        public static bool IsAlive(int seed, int x, int y, float aliveProbability) =>
            IsAlive(unchecked((uint)seed), x, y, Threshold(aliveProbability));
    }
}
