using System.Collections.Generic;
using GameOfLife.Core;
using NUnit.Framework;

namespace GameOfLife.Tests
{
    /// <summary>
    /// 构型数据自检：数量、分类配比、包围盒、坐标合法性。
    /// 题目要求"2 静物 + 2 振荡器 + 2 循环震荡"，这里把它固化成断言。
    /// </summary>
    [TestFixture]
    public class PatternLibraryTests
    {
        [Test]
        public void Library_ContainsExactlySixPatterns()
        {
            Assert.AreEqual(6, PatternLibrary.Count, "题目要求恰好 6 种预制构型");
        }

        [Test]
        public void Library_HasTwoOfEachCategory()
        {
            var counts = new Dictionary<PatternCategory, int>();
            foreach (PatternDefinition pattern in PatternLibrary.All)
            {
                counts.TryGetValue(pattern.Category, out int current);
                counts[pattern.Category] = current + 1;
            }

            Assert.AreEqual(2, counts.GetValueOrDefault(PatternCategory.StillLife), "需要 2 种稳定静物");
            Assert.AreEqual(2, counts.GetValueOrDefault(PatternCategory.Oscillator), "需要 2 种振荡器");
            Assert.AreEqual(2, counts.GetValueOrDefault(PatternCategory.Spaceship), "需要 2 种循环震荡/飞船");
        }

        [Test]
        public void Library_NamesAreUniqueAndResolvable()
        {
            var seen = new HashSet<string>();
            var seenDisplay = new HashSet<string>();

            foreach (PatternDefinition pattern in PatternLibrary.All)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(pattern.Name), "构型名不能为空");
                Assert.IsTrue(seen.Add(pattern.Name), $"构型名重复：{pattern.Name}");
                Assert.IsFalse(string.IsNullOrWhiteSpace(pattern.DisplayName), $"{pattern.Name} 缺少中文显示名");
                Assert.IsTrue(seenDisplay.Add(pattern.DisplayName), $"中文显示名重复：{pattern.DisplayName}");

                Assert.AreSame(pattern, PatternLibrary.Get(pattern.Name), "按名索引应返回同一实例");
                Assert.AreSame(pattern, PatternLibrary.Get(pattern.Name.ToUpperInvariant()), "按名索引应忽略大小写");
            }
        }

        [Test]
        public void Get_UnknownName_Throws()
        {
            Assert.Throws<System.ArgumentException>(() => PatternLibrary.Get("NotAPattern"));
            Assert.AreEqual(-1, PatternLibrary.IndexOf("NotAPattern"));
        }

        [Test]
        public void EveryPattern_HasUniqueCellsInsideItsBoundingBox()
        {
            foreach (PatternDefinition pattern in PatternLibrary.All)
            {
                var occupied = new HashSet<GridPos>();

                foreach (GridPos cell in pattern.Cells)
                {
                    Assert.GreaterOrEqual(cell.X, 0, $"{pattern.Name}: x 不能为负");
                    Assert.GreaterOrEqual(cell.Y, 0, $"{pattern.Name}: y 不能为负");
                    Assert.Less(cell.X, pattern.Width, $"{pattern.Name}: x 超出包围盒");
                    Assert.Less(cell.Y, pattern.Height, $"{pattern.Name}: y 超出包围盒");
                    Assert.IsTrue(occupied.Add(cell), $"{pattern.Name}: 坐标重复 {cell}");
                }

                Assert.GreaterOrEqual(pattern.Width, 1);
                Assert.GreaterOrEqual(pattern.Height, 1);
            }
        }

        [TestCase("Block", 2, 2, 4, 1)]
        [TestCase("Beehive", 4, 3, 6, 1)]
        [TestCase("Blinker", 3, 1, 3, 2)]
        [TestCase("Toad", 4, 2, 6, 2)]
        [TestCase("Glider", 3, 3, 5, 4)]
        [TestCase("LWSS", 5, 4, 9, 4)]
        public void KnownPatterns_HaveExpectedShape(string name, int width, int height, int cells, int period)
        {
            PatternDefinition pattern = PatternLibrary.Get(name);

            Assert.AreEqual(width, pattern.Width, $"{name} 宽度");
            Assert.AreEqual(height, pattern.Height, $"{name} 高度");
            Assert.AreEqual(cells, pattern.Cells.Count, $"{name} 活细胞数");
            Assert.AreEqual(period, pattern.Period, $"{name} 周期");
        }

        [Test]
        public void StillLife_MustHavePeriodOne()
        {
            Assert.Throws<System.ArgumentException>(() => new PatternDefinition(
                "Bad", PatternCategory.StillLife, 2, new GridPos(0, 0), displayName: null, new GridPos(0, 0)));
        }

        [Test]
        public void Spaceship_MustHaveNonZeroDisplacement()
        {
            Assert.Throws<System.ArgumentException>(() => new PatternDefinition(
                "Bad", PatternCategory.Spaceship, 4, new GridPos(0, 0), displayName: null, new GridPos(0, 0)));
        }
    }
}
