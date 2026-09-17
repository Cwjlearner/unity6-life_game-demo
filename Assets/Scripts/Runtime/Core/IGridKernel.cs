namespace GameOfLife.Core
{
    /// <summary>
    /// 网格演化的统一契约，CPU 与 GPU 两条实现路径都实现它。
    /// <para>
    /// 刻意不提供 GetCell：GPU 实现无法同步回答单点查询。
    /// 读状态统一走 <see cref="Snapshot"/>——GPU 侧内部用异步回读实现，
    /// 仅用于验证、统计与测试，不要放在每帧热路径上。
    /// </para>
    /// </summary>
    public interface IGridKernel
    {
        int Width { get; }
        int Height { get; }

        /// <summary>环面边界（true）或死边界（false）。</summary>
        bool WrapEdges { get; }

        /// <summary>已演化的代数，Clear 后归零。</summary>
        int Generation { get; }

        /// <summary>当前活细胞数。</summary>
        int Population { get; }

        /// <summary>清空为全死状态，并把代数归零。</summary>
        void Clear();

        /// <summary>
        /// 用确定性随机状态填充整个网格（同一 seed 得到同一张棋盘），并把代数归零。
        /// <paramref name="aliveProbability"/> 为初始存活密度，常用 0.3 左右最容易长出丰富的结构。
        /// </summary>
        void FillRandom(int seed, float aliveProbability);

        /// <summary>把预制构型盖印到网格，(originX, originY) 对应构型包围盒左下角。</summary>
        void StampPattern(PatternDefinition pattern, int originX, int originY);

        /// <summary>演化一代（B3/S23）。</summary>
        void Step();

        /// <summary>取当前状态快照。</summary>
        GridSnapshot Snapshot();
    }
}
