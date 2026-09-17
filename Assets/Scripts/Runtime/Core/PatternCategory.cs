namespace GameOfLife.Core
{
    /// <summary>构型分类，对应题目要求的三种行为。</summary>
    public enum PatternCategory
    {
        /// <summary>稳定静物：周期 1，形状不随时间变化。</summary>
        StillLife,

        /// <summary>振荡器：周期 &gt; 1，形状往复但不位移。</summary>
        Oscillator,

        /// <summary>飞船/循环震荡：周期 &gt; 1，且每个周期产生固定位移。</summary>
        Spaceship
    }
}
