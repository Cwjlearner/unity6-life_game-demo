using System.Collections.Generic;
using System.Text;
using GameOfLife.Core;
using NUnit.Framework;

namespace GameOfLife.Tests
{
    /// <summary>
    /// 题目核心要求的直接验证：6 种构型在真实演化中必须表现出
    /// "稳定 / 振荡 / 循环震荡（位移）"三种行为。
    /// <para>
    /// 期望的周期与位移来自独立的 Python 模拟器（tools/verify_patterns.py），
    /// 这里在 C# 实现上复验——两边不一致就说明实现或数据有一处错了。
    /// </para>
    /// </summary>
    [TestFixture]
    public class PatternBehaviourTests
    {
        private const int GridSize = 64;
        private const int Origin = 30;

        [TestCase("Block")]
        [TestCase("Beehive")]
        public void StillLife_IsUnchangedAfterOneGeneration(string name)
        {
            PatternDefinition pattern = PatternLibrary.Get(name);
            List<GridSnapshot> history = Run(pattern, generations: 2);

            GridSnapshot first = history[0];
            GridSnapshot second = history[1];

            Assert.AreEqual(first.Population, second.Population, $"{name} 活细胞数应保持不变");
            Assert.AreEqual(first.ComputeHash(), second.ComputeHash(),
                $"{name} 应完全不变。实际：\n{second.ToTrimmedAscii()}");
        }

        [TestCase("Blinker")]
        [TestCase("Toad")]
        public void Oscillator_ReturnsToInitialStateAfterPeriod_ButChangesInBetween(string name)
        {
            PatternDefinition pattern = PatternLibrary.Get(name);
            List<GridSnapshot> history = Run(pattern, generations: pattern.Period * 2);

            Assert.AreNotEqual(history[0].ComputeHash(), history[1].ComputeHash(),
                $"{name} 第 1 代必须与初始不同，否则它是静物而不是振荡器");

            Assert.AreEqual(history[0].ComputeHash(), history[pattern.Period].ComputeHash(),
                $"{name} 应在 {pattern.Period} 代后回到初始形状。第 {pattern.Period} 代实际：\n" +
                history[pattern.Period].ToTrimmedAscii());

            Assert.AreEqual(history[0].ComputeHash(), history[pattern.Period * 2].ComputeHash(),
                $"{name} 应在第二个周期后再次回到初始形状（排除巧合）");

            Assert.AreEqual(history[0].Population, history[pattern.Period].Population,
                $"{name} 振荡过程中活细胞数应守恒");
        }

        [TestCase("Glider", 1, -1)]
        [TestCase("LWSS", -2, 0)]
        public void Spaceship_ReturnsToInitialShape_TranslatedByConstantOffset(
            string name, int expectedDx, int expectedDy)
        {
            PatternDefinition pattern = PatternLibrary.Get(name);
            Assert.AreEqual(expectedDx, pattern.Displacement.X, $"{name} 期待位移 dx 与构型数据不符");
            Assert.AreEqual(expectedDy, pattern.Displacement.Y, $"{name} 期待位移 dy 与构型数据不符");

            List<GridSnapshot> history = Run(pattern, generations: pattern.Period * 2);

            Assert.AreNotEqual(history[0].ComputeHash(), history[1].ComputeHash(),
                $"{name} 第 1 代必须已经变化");

            // 关键判定：不是"相等"，而是"平移后相等"。飞船永远不会回到原位。
            Assert.IsTrue(
                PatternCompare.MatchesTranslated(history[0], history[pattern.Period], expectedDx, expectedDy, wrapEdges: true),
                $"{name} 应在 {pattern.Period} 代后平移 ({expectedDx},{expectedDy})。第 {pattern.Period} 代实际：\n" +
                history[pattern.Period].ToTrimmedAscii());

            Assert.IsFalse(
                PatternCompare.MatchesTranslated(history[0], history[pattern.Period], 0, 0, wrapEdges: true),
                $"{name} 不应原地不动");

            Assert.AreEqual(history[0].Population, history[pattern.Period].Population,
                $"{name} 每周期结束时活细胞数应回到初始值");
        }

        [TestCase("Block", 1, 0, 0)]
        [TestCase("Beehive", 1, 0, 0)]
        [TestCase("Blinker", 2, 0, 0)]
        [TestCase("Toad", 2, 0, 0)]
        [TestCase("Glider", 4, 1, -1)]
        [TestCase("LWSS", 4, -2, 0)]
        public void RuntimeDetector_RecoversExpectedPeriodAndDisplacement(
            string name, int expectedPeriod, int expectedDx, int expectedDy)
        {
            PatternDefinition pattern = PatternLibrary.Get(name);
            List<GridSnapshot> history = Run(pattern, generations: expectedPeriod * 2);

            bool found = PatternCompare.TryDetectPeriod(
                history, maxPeriod: 8, maxDisplacement: 4, out int period, out GridPos displacement);

            Assert.IsTrue(found, $"{name}: 运行期周期检测应能识别该构型。历史：\n{Describe(history)}");
            Assert.AreEqual(expectedPeriod, period, $"{name} 检测到的周期不符");
            Assert.AreEqual(expectedDx, displacement.X, $"{name} 检测到的位移 dx 不符");
            Assert.AreEqual(expectedDy, displacement.Y, $"{name} 检测到的位移 dy 不符");
        }

        [Test]
        public void LWSS_PopulationPulsesWithinPeriod()
        {
            // LWSS 的中间代会出现 12 个活细胞（独立模拟器给出的群体序列 9/12/9/12/9）。
            // 这条用例锁定"周期内形态确实在变"，避免把飞船误实现成滑动的静物。
            PatternDefinition pattern = PatternLibrary.Get("LWSS");
            List<GridSnapshot> history = Run(pattern, generations: pattern.Period);

            var populations = new List<int>();
            foreach (GridSnapshot snapshot in history)
                populations.Add(snapshot.Population);

            CollectionAssert.AreEqual(new[] { 9, 12, 9, 12, 9 }, populations,
                "LWSS 的群体数量序列应为 9/12/9/12/9");
        }

        [Test]
        public void AllPatterns_AreStampedFullyInsideGrid()
        {
            foreach (PatternDefinition pattern in PatternLibrary.All)
            {
                var grid = new GridModel(GridSize, GridSize);
                grid.StampPattern(pattern, Origin, Origin);

                Assert.AreEqual(pattern.Cells.Count, grid.Population,
                    $"{pattern.Name} 盖印后活细胞数应等于构型定义，说明没有越界或重叠");
            }
        }

        [Test]
        public void AnalysisPath_SnapshotThenCpuThenDetect_RecoversGliderPeriod()
        {
            // 复现 LifeController.AnalyzeCurrentPattern 的取数路径：
            // 状态快照 → 载入 CPU 参考实现 → 向前演化 → 周期检测。
            // 这条路径上的每一步都与 GPU 路径共用同一批类型，回归时先在这里断。
            var source = new GridModel(GridSize, GridSize, wrapEdges: true);
            source.StampPattern(PatternLibrary.Get("Glider"), Origin, Origin);

            var model = new GridModel(GridSize, GridSize, wrapEdges: true);
            model.LoadFrom(source.Snapshot());

            var history = new List<GridSnapshot> { model.Snapshot() };
            for (int i = 0; i < 8; i++)
            {
                model.Step();
                history.Add(model.Snapshot());
            }

            Assert.IsTrue(
                PatternCompare.TryDetectPeriod(history, maxPeriod: 8, maxDisplacement: 4,
                    out int period, out GridPos displacement),
                "分析路径应能检出周期");

            Assert.AreEqual(4, period, "滑翔机周期应为 4");
            Assert.AreEqual(1, displacement.X, "滑翔机每周期位移 dx 应为 1");
            Assert.AreEqual(-1, displacement.Y, "滑翔机每周期位移 dy 应为 -1");
        }

        private static List<GridSnapshot> Run(PatternDefinition pattern, int generations)
        {
            var grid = new GridModel(GridSize, GridSize, wrapEdges: true);
            grid.StampPattern(pattern, Origin, Origin);

            var history = new List<GridSnapshot> { grid.Snapshot() };
            for (int i = 0; i < generations; i++)
            {
                grid.Step();
                history.Add(grid.Snapshot());
            }

            return history;
        }

        private static string Describe(IReadOnlyList<GridSnapshot> history)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < history.Count; i++)
            {
                sb.Append("gen ").Append(i)
                  .Append(" pop=").Append(history[i].Population)
                  .Append(" hash=").Append(history[i].ComputeHash().ToString("x16"))
                  .AppendLine();
            }

            return sb.ToString();
        }
    }
}
