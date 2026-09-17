namespace GameOfLife.Gpu
{
    /// <summary>演进 kernel 的实现档位。</summary>
    public enum GpuStepMode
    {
        /// <summary>基础版：每个线程直接从显存采样 8 个邻居，numthreads(8,8,1)。</summary>
        Basic = 0,

        /// <summary>档一：线程组先把含 halo 的整块载入 groupshared，邻居从共享内存读，numthreads(16,16,1)。</summary>
        SharedMemory = 1,

        /// <summary>
        /// 档二：一个 texel 的 RGBA 四通道各存一个细胞，纹理面积与线程数各降到 1/4。
        /// 要求网格宽度是 4 的倍数。
        /// </summary>
        PackedChannels = 2,

        /// <summary>
        /// 档三：位打包，1 个 uint 承载 32 个细胞，用移位与位并行加法统计邻居。
        /// 要求网格宽度是 32 的倍数。
        /// </summary>
        BitPacked = 3,
    }
}
