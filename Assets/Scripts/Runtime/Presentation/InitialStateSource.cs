namespace GameOfLife.Presentation
{
    /// <summary>重置（以及进入 Play）时，棋盘上的初始状态从哪里来。</summary>
    public enum InitialStateSource
    {
        /// <summary>从 6 种预制构型里取当前选中的那一种。</summary>
        PresetPattern = 0,

        /// <summary>按固定种子生成随机棋盘：可以复现、可以和别人对同一个局面。</summary>
        RandomWithSeed = 1,

        /// <summary>每次重置都换一颗新种子：真正的"随机模拟"，每次都不一样。</summary>
        RandomEachTime = 2,
    }
}
