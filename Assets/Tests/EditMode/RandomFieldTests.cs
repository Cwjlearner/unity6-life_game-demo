using GameOfLife.Core;
using NUnit.Framework;

namespace GameOfLife.Tests
{
    /// <summary>
    /// 确定性随机初始状态的 CPU 侧验证。
    /// 黄金值来自工具的独立 Python 实现（同一套整数哈希重写一遍），用于锁住 C# 实现；
    /// HLSL 那一侧由 GpuParityTests 的随机填充用例锁住。
    /// 三方（Python / C# / HLSL）必须给出同一张棋盘。
    /// </summary>
    [TestFixture]
    public class RandomFieldTests
    {
        [TestCase(1u, 0u, 0u, 1224854503u)]
        [TestCase(1u, 1u, 0u, 3706830254u)]
        [TestCase(0u, 0u, 1u, 3778847611u)]
        [TestCase(12345u, 5u, 7u, 3546727218u)]
        [TestCase(0xDEADBEEFu, 255u, 255u, 422393475u)]
        [TestCase(20260917u, 63u, 31u, 3375578950u)]
        public void Hash_MatchesIndependentReferenceValues(uint seed, uint x, uint y, uint expected)
        {
            Assert.AreEqual(expected, RandomField.Hash(seed, x, y),
                "哈希实现被改动过：请同步更新独立参考实现与 HLSL 中的 RandomHash");
        }

        [Test]
        public void SameSeed_ProducesIdenticalGrids()
        {
            var a = new GridModel(64, 64, wrapEdges: true);
            var b = new GridModel(64, 64, wrapEdges: true);

            a.FillRandom(20260917, 0.32f);
            b.FillRandom(20260917, 0.32f);

            Assert.AreEqual(a.Population, b.Population, "同种子应得到相同活细胞数");
            Assert.AreEqual(a.Snapshot().ComputeHash(), b.Snapshot().ComputeHash(), "同种子应得到逐位相同的棋盘");
        }

        [Test]
        public void DifferentSeeds_ProduceDifferentGrids()
        {
            var a = new GridModel(64, 64, wrapEdges: true);
            var b = new GridModel(64, 64, wrapEdges: true);

            a.FillRandom(1, 0.32f);
            b.FillRandom(2, 0.32f);

            Assert.AreNotEqual(a.Snapshot().ComputeHash(), b.Snapshot().ComputeHash(), "不同种子应得到不同棋盘");
        }

        /// <summary>
        /// 密度检查同时锁住哈希实现：这个数字由独立实现算得，
        /// 与 C# 完全一致说明两边逐格结果相同，而不只是数量相近。
        /// </summary>
        [Test]
        public void Density_MatchesIndependentReferenceCount()
        {
            var grid = new GridModel(64, 64, wrapEdges: true);
            grid.FillRandom(20260917, 0.32f);

            Assert.AreEqual(1295, grid.Population,
                "64x64 / seed=20260917 / density=0.32 的活细胞数应与独立实现一致");
        }

        [Test]
        public void ZeroProbability_ProducesEmptyGrid()
        {
            var grid = new GridModel(32, 32, wrapEdges: true);
            grid.FillRandom(12345, 0f);

            Assert.AreEqual(0, grid.Population, "密度为 0 时应全为死细胞");
        }

        [Test]
        public void FillRandom_ResetsGenerationAndClearsPreviousState()
        {
            var grid = new GridModel(32, 32, wrapEdges: true);
            grid.StampPattern(PatternLibrary.Get("Glider"), 5, 5);
            grid.Step();
            grid.Step();

            Assert.AreEqual(2, grid.Generation);

            grid.FillRandom(777, 0.3f);
            Assert.AreEqual(0, grid.Generation, "填充随机状态后代数应归零");

            var reference = new GridModel(32, 32, wrapEdges: true);
            reference.FillRandom(777, 0.3f);

            Assert.AreEqual(reference.Snapshot().ComputeHash(), grid.Snapshot().ComputeHash(),
                "填充后不应残留旧状态");
        }
    }
}
