using System;
using System.Collections.Generic;

namespace GameOfLife.Core
{
    /// <summary>
    /// 题目要求的 6 种预制构型：2 静物 + 2 振荡器 + 2 飞船。
    /// <para>
    /// 坐标与周期/位移由 Python 独立模拟验证（见 tools/verify_patterns.py），
    /// 再由 Assets/Tests/EditMode/PatternBehaviourTests.cs 在 Unity 侧复验。
    /// </para>
    /// </summary>
    public static class PatternLibrary
    {
        private static readonly PatternDefinition[] s_All =
        {
            // ---------- 稳定静物 (period = 1) ----------
            new PatternDefinition(
                "Block",
                PatternCategory.StillLife,
                period: 1,
                displacement: new GridPos(0, 0),
                displayName: "方块",
                new GridPos(0, 0), new GridPos(1, 0),
                new GridPos(0, 1), new GridPos(1, 1)),

            new PatternDefinition(
                "Beehive",
                PatternCategory.StillLife,
                period: 1,
                displacement: new GridPos(0, 0),
                displayName: "蜂巢",
                new GridPos(1, 0), new GridPos(2, 0),
                new GridPos(0, 1), new GridPos(3, 1),
                new GridPos(1, 2), new GridPos(2, 2)),

            // ---------- 振荡器 (period = 2) ----------
            new PatternDefinition(
                "Blinker",
                PatternCategory.Oscillator,
                period: 2,
                displacement: new GridPos(0, 0),
                displayName: "闪烁灯",
                new GridPos(0, 0), new GridPos(1, 0), new GridPos(2, 0)),

            new PatternDefinition(
                "Toad",
                PatternCategory.Oscillator,
                period: 2,
                displacement: new GridPos(0, 0),
                displayName: "蟾蜍",
                new GridPos(1, 0), new GridPos(2, 0), new GridPos(3, 0),
                new GridPos(0, 1), new GridPos(1, 1), new GridPos(2, 1)),

            // ---------- 飞船 / 循环震荡 (period = 4) ----------
            new PatternDefinition(
                "Glider",
                PatternCategory.Spaceship,
                period: 4,
                displacement: new GridPos(1, -1),
                displayName: "滑翔机",
                new GridPos(1, 2),
                new GridPos(2, 1),
                new GridPos(0, 0), new GridPos(1, 0), new GridPos(2, 0)),

            new PatternDefinition(
                "LWSS",
                PatternCategory.Spaceship,
                period: 4,
                displacement: new GridPos(-2, 0),
                displayName: "轻型飞船",
                new GridPos(1, 3), new GridPos(4, 3),
                new GridPos(0, 2),
                new GridPos(0, 1), new GridPos(4, 1),
                new GridPos(0, 0), new GridPos(1, 0), new GridPos(2, 0), new GridPos(3, 0)),
        };

        /// <summary>全部构型，顺序即 UI 中的展示顺序。</summary>
        public static IReadOnlyList<PatternDefinition> All => s_All;

        public static int Count => s_All.Length;

        public static PatternDefinition Get(int index)
        {
            if (index < 0 || index >= s_All.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"构型索引必须在 [0,{s_All.Length}) 内");
            return s_All[index];
        }

        public static PatternDefinition Get(string name)
        {
            if (!TryGet(name, out PatternDefinition pattern))
                throw new ArgumentException($"未知构型：{name}", nameof(name));
            return pattern;
        }

        public static int IndexOf(string name)
        {
            for (int i = 0; i < s_All.Length; i++)
            {
                if (string.Equals(s_All[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        public static bool TryGet(string name, out PatternDefinition pattern)
        {
            int index = IndexOf(name);
            pattern = index >= 0 ? s_All[index] : null;
            return index >= 0;
        }
    }
}
