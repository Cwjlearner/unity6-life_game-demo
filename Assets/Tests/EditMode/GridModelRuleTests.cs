using System.Collections.Generic;
using GameOfLife.Core;
using NUnit.Framework;

namespace GameOfLife.Tests
{
    /// <summary>
    /// 规则本身（B3/S23）、边界语义、快照与打包转换的正确性。
    /// 这些用例不涉及任何构型，纯粹锁死"一代演化"的语义。
    /// </summary>
    [TestFixture]
    public class GridModelRuleTests
    {
        /// <summary>
        /// 规则穷举验证：3×3 邻域一共只有 512 种组合，逐一比对中心细胞的下一代状态。
        /// 这比"挑几个例子"强得多——它覆盖了所有可能出现的邻居分布，
        /// 等价于把教科书上的 B3/S23 规则表逐行核对一遍。
        /// </summary>
        [Test]
        public void Rule_IsExhaustivelyCorrect_ForAll512Neighbourhoods()
        {
            const int combinations = 512;   // 2^9

            for (int mask = 0; mask < combinations; mask++)
            {
                var grid = new GridModel(5, 5, wrapEdges: false);
                int liveNeighbours = 0;
                int bit = 0;

                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++, bit++)
                    {
                        if ((mask & (1 << bit)) == 0) continue;

                        grid.SetCell(2 + dx, 2 + dy, true);
                        if (dx != 0 || dy != 0) liveNeighbours++;
                    }
                }

                bool selfAlive = grid.GetCell(2, 2);

                // 教科书规则（B3/S23）：
                //   活细胞有 2 或 3 个活邻居 -> 存活；否则死亡
                //   死细胞恰好 3 个活邻居     -> 诞生
                bool expected = selfAlive
                    ? liveNeighbours == 2 || liveNeighbours == 3
                    : liveNeighbours == 3;

                // 中心格的下一代只取决于当前的 3×3 邻域，因此整盘演化一代后读中心即可。
                grid.Step();

                Assert.AreEqual(
                    expected,
                    grid.GetCell(2, 2),
                    $"邻域组合 mask={mask}（自身{(selfAlive ? "活" : "死")}，活邻居 {liveNeighbours} 个）" +
                    "的下一代结果与 B3/S23 规则不符");
            }
        }

        [Test]
        public void EmptyGrid_StaysEmpty()
        {
            var grid = new GridModel(8, 8);
            grid.Step();

            Assert.AreEqual(0, grid.Population, "空网格演化后应仍为空");
        }

        [Test]
        public void SingleCell_DiesOfUnderpopulation()
        {
            var grid = new GridModel(8, 8);
            grid.SetCell(4, 4, true);

            grid.Step();

            Assert.AreEqual(0, grid.Population, "孤立细胞应因邻居不足而死亡");
        }

        [Test]
        public void ThreeInARow_BecomesVerticalBlinker()
        {
            var grid = new GridModel(8, 8);
            grid.SetCell(3, 4, true);
            grid.SetCell(4, 4, true);
            grid.SetCell(5, 4, true);

            grid.Step();

            var expected = new GridModel(8, 8);
            expected.SetCell(4, 3, true);
            expected.SetCell(4, 4, true);
            expected.SetCell(4, 5, true);

            Assert.AreEqual(expected.Snapshot().ComputeHash(), grid.Snapshot().ComputeHash(),
                $"横条应变为竖条。实际：\n{grid.Snapshot().ToTrimmedAscii()}");
        }

        [Test]
        public void FullThreeByThree_SurvivesAtCorners_AndGivesBirthAroundEdges()
        {
            // 角上 3 个邻居 -> 存活；边中 5 个邻居、中心 8 个邻居 -> 因过密死亡；
            // 同时紧贴四边的 4 个外部空位各恰好有 3 个邻居 -> 诞生。净结果 8 个活细胞。
            // （这条期望值由 tools/verify_patterns.py 独立模拟仲裁过一次：
            //   最初手算的"只剩 4 个角"是错的，错在漏算了外圈诞生。）
            var grid = new GridModel(8, 8);
            for (int y = 3; y <= 5; y++)
            {
                for (int x = 3; x <= 5; x++)
                    grid.SetCell(x, y, true);
            }

            grid.Step();

            var snapshot = grid.Snapshot();
            Assert.AreEqual(8, snapshot.Population, $"3x3 实心块演化后应为 8 个活细胞。实际：\n{snapshot.ToTrimmedAscii()}");

            Assert.IsTrue(snapshot.Get(3, 3), "左下角应存活");
            Assert.IsTrue(snapshot.Get(5, 3), "右下角应存活");
            Assert.IsTrue(snapshot.Get(3, 5), "左上角应存活");
            Assert.IsTrue(snapshot.Get(5, 5), "右上角应存活");

            Assert.IsTrue(snapshot.Get(4, 2), "(4,2) 外部空位应诞生");
            Assert.IsTrue(snapshot.Get(4, 6), "(4,6) 外部空位应诞生");
            Assert.IsTrue(snapshot.Get(2, 4), "(2,4) 外部空位应诞生");
            Assert.IsTrue(snapshot.Get(6, 4), "(6,4) 外部空位应诞生");

            Assert.IsFalse(snapshot.Get(4, 4), "中心应因过密而死亡");
            Assert.IsFalse(snapshot.Get(4, 3), "边中应因过密而死亡");
        }

        [Test]
        public void WrapEdges_BlinkerCrossesSeam()
        {
            // 跨越左右接缝的横条：x = W-1, 0, 1（y = 4）
            var grid = new GridModel(8, 8, wrapEdges: true);
            grid.SetCell(7, 4, true);
            grid.SetCell(0, 4, true);
            grid.SetCell(1, 4, true);

            grid.Step();

            var snapshot = grid.Snapshot();
            Assert.AreEqual(3, snapshot.Population, "环面边界下接缝处的横条应正常演化为竖条");
            Assert.IsTrue(snapshot.Get(0, 3), $"应出现 (0,3)。实际：\n{snapshot.ToTrimmedAscii()}");
            Assert.IsTrue(snapshot.Get(0, 4), $"应出现 (0,4)。实际：\n{snapshot.ToTrimmedAscii()}");
            Assert.IsTrue(snapshot.Get(0, 5), $"应出现 (0,5)。实际：\n{snapshot.ToTrimmedAscii()}");
        }

        [Test]
        public void DeadBoundary_NeighbourQueryOutsideIsDead()
        {
            var bounded = new GridModel(8, 8, wrapEdges: false);
            Assert.IsFalse(bounded.GetCell(-1, 0), "死边界模式下越界读取应视为死细胞");

            var toroidal = new GridModel(8, 8, wrapEdges: true);
            toroidal.SetCell(7, 0, true);
            Assert.IsTrue(toroidal.GetCell(-1, 0), "环面模式下越界读取应回绕到 (7,0)");
        }

        [Test]
        public void SetCell_OutOfRange_Throws()
        {
            var grid = new GridModel(4, 4);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => grid.SetCell(4, 0, true));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => grid.SetCell(0, -1, true));
        }

        [Test]
        public void Hash_DistinguishesDifferentStates_SameForIdentical()
        {
            var a = new GridModel(8, 8);
            var b = new GridModel(8, 8);
            a.SetCell(1, 1, true);
            b.SetCell(1, 1, true);

            Assert.AreEqual(a.Snapshot().ComputeHash(), b.Snapshot().ComputeHash(), "相同状态应有相同哈希");

            b.SetCell(5, 5, true);
            Assert.AreNotEqual(a.Snapshot().ComputeHash(), b.Snapshot().ComputeHash(), "不同状态应有不同哈希");
        }

        [Test]
        public void Hash_IgnoresGenerationCounter()
        {
            // 哈希只描述状态：稳定态相邻两代哈希必然相同，这正是周期判定的基础。
            var grid = new GridModel(8, 8);
            grid.StampPattern(PatternLibrary.Get("Block"), 3, 3);

            ulong before = grid.Snapshot().ComputeHash();
            grid.Step();
            ulong after = grid.Snapshot().ComputeHash();

            Assert.AreEqual(1, grid.Generation, "代数应递增");
            Assert.AreEqual(before, after, "稳定态相邻两代哈希应相同（哈希不含代数）");
        }

        [Test]
        public void Snapshot_RoundTripsThroughLoadFrom()
        {
            var source = new GridModel(16, 16);
            source.StampPattern(PatternLibrary.Get("Glider"), 5, 5);
            source.Step();
            source.Step();

            var restored = new GridModel(16, 16);
            restored.LoadFrom(source.Snapshot());

            Assert.AreEqual(source.Population, restored.Population);
            Assert.AreEqual(source.Generation, restored.Generation);
            Assert.AreEqual(source.Snapshot().ComputeHash(), restored.Snapshot().ComputeHash());
        }

        /// <summary>
        /// 验证 GPU 回读路径：把状态按 GPU 的 32 位行打包方式重排后，
        /// 构造出的快照必须与 CPU 快照等价。这是 CPU/GPU 交叉验证的地基。
        /// </summary>
        [Test]
        public void FromPackedBits_MatchesFromCells_ForNonMultipleOf32Width()
        {
            const int width = 37;   // 刻意取非 32 的倍数，覆盖行尾填充
            const int height = 11;

            var grid = new GridModel(width, height, wrapEdges: false);
            grid.StampPattern(PatternLibrary.Get("Toad"), 30, 4);
            grid.SetCell(0, 0, true);
            grid.SetCell(36, 10, true);
            grid.Step();

            GridSnapshot cpuSnapshot = grid.Snapshot();

            int gpuWordsPerRow = (width + 31) / 32;
            var packed = new uint[gpuWordsPerRow * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!cpuSnapshot.Get(x, y)) continue;
                    packed[y * gpuWordsPerRow + (x >> 5)] |= 1u << (x & 31);
                }
            }

            GridSnapshot gpuSnapshot =
                GridSnapshot.FromPackedBits(width, height, packed, cpuSnapshot.Population, cpuSnapshot.Generation);

            Assert.AreEqual(cpuSnapshot.ComputeHash(), gpuSnapshot.ComputeHash(),
                "GPU 行打包方式换算回来的状态必须与 CPU 快照一致");
        }

        [Test]
        public void TryDetectPeriod_OnStillLife_ReturnsPeriodOne()
        {
            var history = RunHistory(PatternLibrary.Get("Block"), generations: 3);

            bool found = PatternCompare.TryDetectPeriod(history, maxPeriod: 8, maxDisplacement: 4,
                out int period, out GridPos displacement);

            Assert.IsTrue(found, "稳定态应能被检出");
            Assert.AreEqual(1, period);
            Assert.AreEqual(0, displacement.X);
            Assert.AreEqual(0, displacement.Y);
        }

        private static List<GridSnapshot> RunHistory(PatternDefinition pattern, int generations)
        {
            var grid = new GridModel(64, 64, wrapEdges: true);
            grid.StampPattern(pattern, 30, 30);

            var history = new List<GridSnapshot> { grid.Snapshot() };
            for (int i = 0; i < generations; i++)
            {
                grid.Step();
                history.Add(grid.Snapshot());
            }

            return history;
        }
    }
}
