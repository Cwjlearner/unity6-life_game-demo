using GameOfLife.Core;
using GameOfLife.Gpu;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GameOfLife.Tests
{
    /// <summary>
    /// GPU 与 CPU 的一致性验证。这是三档性能优化都不能跳过的一关：
    /// GPU 算错时画面常常"看起来还在动"，只有逐代比对哈希才能发现。
    /// 需要图形设备，在 -nographics 批处理下会被跳过（该模式同时验证了 CPU 回退路径可用）。
    /// </summary>
    [TestFixture]
    public class GpuParityTests
    {
        private const string ShaderPath = "Assets/Shaders/LifePingPong.compute";

        private ComputeShader _shader;

        [OneTimeSetUp]
        public void LoadShader()
        {
            _shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            Assert.IsNotNull(_shader, $"未找到 ComputeShader：{ShaderPath}");
        }

        [TestCase(GpuStepMode.Basic)]
        [TestCase(GpuStepMode.SharedMemory)]
        [TestCase(GpuStepMode.PackedChannels)]
        [TestCase(GpuStepMode.BitPacked)]
        public void AllPatterns_GpuMatchesCpu_EveryGeneration(GpuStepMode mode)
        {
            RequireGpu();

            const int size = 64;
            const int origin = 30;

            foreach (PatternDefinition pattern in PatternLibrary.All)
            {
                var cpu = new GridModel(size, size, wrapEdges: true);
                var gpu = new GpuGridKernel(_shader, size, size, wrapEdges: true, stepMode: mode);

                try
                {
                    cpu.StampPattern(pattern, origin, origin);
                    gpu.StampPattern(pattern, origin, origin);

                    string label = $"{mode}/{pattern.Name}";
                    AssertMatch(cpu.Snapshot(), gpu.Snapshot(), $"{label} 第 0 代");

                    int generations = pattern.Period * 2;
                    for (int i = 1; i <= generations; i++)
                    {
                        cpu.Step();
                        gpu.Step();

                        AssertMatch(cpu.Snapshot(), gpu.Snapshot(), $"{label} 第 {i} 代");
                    }

                    Assert.AreEqual(cpu.Generation, gpu.Generation, $"{label}: 代数计数器不一致");
                }
                finally
                {
                    gpu.Dispose();
                }
            }
        }

        [TestCase(GpuStepMode.Basic, 37, 23)]
        [TestCase(GpuStepMode.SharedMemory, 37, 23)]
        [TestCase(GpuStepMode.PackedChannels, 36, 23)]
        [TestCase(GpuStepMode.BitPacked, 96, 23)]
        public void NonMultipleOfGroupSize_GpuMatchesCpu(GpuStepMode mode, int width, int height)
        {
            RequireGpu();

            // 尺寸刻意都不是线程组大小的倍数，专门覆盖越界线程与行尾填充。
            // 档二要求宽度是 4 的倍数（用 36），档三要求是 32 的倍数（用 96，
            // 此时每行 3 个字，正好覆盖档三最易出错的跨字边界；高度 23 不是线程组大小的倍数）。
            var cpu = new GridModel(width, height, wrapEdges: true);
            var gpu = new GpuGridKernel(_shader, width, height, wrapEdges: true, stepMode: mode);

            try
            {
                // 确定性伪随机初始局面，覆盖稠密与稀疏两种情形。
                var random = new System.Random(20260917);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (random.NextDouble() >= 0.35) continue;
                        cpu.SetCell(x, y, true);
                        gpu.StampPattern(SingleCell, x, y);
                    }
                }

                AssertMatch(cpu.Snapshot(), gpu.Snapshot(), "37x23 随机初始局面");

                for (int i = 1; i <= 6; i++)
                {
                    cpu.Step();
                    gpu.Step();
                    AssertMatch(cpu.Snapshot(), gpu.Snapshot(), $"37x23 第 {i} 代");
                }
            }
            finally
            {
                gpu.Dispose();
            }
        }

        [TestCase(GpuStepMode.Basic)]
        [TestCase(GpuStepMode.SharedMemory)]
        [TestCase(GpuStepMode.PackedChannels)]
        [TestCase(GpuStepMode.BitPacked)]
        public void BoundedEdges_GpuMatchesCpu(GpuStepMode mode)
        {
            RequireGpu();

            const int size = 32;
            var cpu = new GridModel(size, size, wrapEdges: false);
            var gpu = new GpuGridKernel(_shader, size, size, wrapEdges: false, stepMode: mode);

            try
            {
                // 滑翔机贴近右上角：死边界下会撞墙解体，两条路径必须给出同样的解体过程。
                PatternDefinition glider = PatternLibrary.Get("Glider");
                cpu.StampPattern(glider, size - 4, size - 4);
                gpu.StampPattern(glider, size - 4, size - 4);

                AssertMatch(cpu.Snapshot(), gpu.Snapshot(), "死边界 第 0 代");

                for (int i = 1; i <= 12; i++)
                {
                    cpu.Step();
                    gpu.Step();
                    AssertMatch(cpu.Snapshot(), gpu.Snapshot(), $"死边界 第 {i} 代");
                }
            }
            finally
            {
                gpu.Dispose();
            }
        }

        [TestCase(GpuStepMode.Basic)]
        [TestCase(GpuStepMode.SharedMemory)]
        [TestCase(GpuStepMode.PackedChannels)]
        [TestCase(GpuStepMode.Basic)]
        [TestCase(GpuStepMode.SharedMemory)]
        [TestCase(GpuStepMode.PackedChannels)]
        [TestCase(GpuStepMode.BitPacked)]
        public void RandomFill_GpuMatchesCpu(GpuStepMode mode)
        {
            RequireGpu();

            const int size = 64;
            const int seed = 20260917;
            const float density = 0.32f;

            var cpu = new GridModel(size, size, wrapEdges: true);
            var gpu = new GpuGridKernel(_shader, size, size, wrapEdges: true, stepMode: mode);

            try
            {
                cpu.FillRandom(seed, density);
                gpu.FillRandom(seed, density);

                // 这条断言同时锁住 HLSL 与 C# 两边的哈希实现必须逐位一致。
                AssertMatch(cpu.Snapshot(), gpu.Snapshot(), $"{mode} 随机填充");
            }
            finally
            {
                gpu.Dispose();
            }
        }

        [TestCase(GpuStepMode.Basic)]
        [TestCase(GpuStepMode.SharedMemory)]
        [TestCase(GpuStepMode.PackedChannels)]
        [TestCase(GpuStepMode.BitPacked)]
        public void RandomSoup_GpuMatchesCpu_EveryGeneration(GpuStepMode mode)
        {
            RequireGpu();

            // 随机汤比稀疏构型更接近压力测试：密度高、结构混乱，边界与跨字都会被频繁命中。
            const int size = 64;
            var cpu = new GridModel(size, size, wrapEdges: true);
            var gpu = new GpuGridKernel(_shader, size, size, wrapEdges: true, stepMode: mode);

            try
            {
                cpu.FillRandom(4242, 0.4f);
                gpu.FillRandom(4242, 0.4f);

                for (int i = 0; i <= 8; i++)
                {
                    AssertMatch(cpu.Snapshot(), gpu.Snapshot(), $"{mode} 随机汤 第 {i} 代");
                    cpu.Step();
                    gpu.Step();
                }
            }
            finally
            {
                gpu.Dispose();
            }
        }

        [TestCase(GpuStepMode.Basic)]
        [TestCase(GpuStepMode.SharedMemory)]
        [TestCase(GpuStepMode.PackedChannels)]
        [TestCase(GpuStepMode.BitPacked)]
        public void DisplayTexture_IsAPlainPictureOfEveryCell(GpuStepMode mode)
        {
            RequireGpu();

            const int size = 64;
            var cpu = new GridModel(size, size, wrapEdges: true);
            var gpu = new GpuGridKernel(_shader, size, size, wrapEdges: true, stepMode: mode);

            try
            {
                cpu.FillRandom(4242, 0.35f);
                gpu.FillRandom(4242, 0.35f);

                RenderTexture display = gpu.DisplayTexture;

                // 给人看的纹理必须是"一个像素一个细胞"：尺寸等于网格尺寸。
                // 打包布局（4 细胞/像素 或 32 细胞/字）如果直接送去显示，
                // 画面会被横向拉伸，并且多个细胞的颜色会混在一起。
                Assert.AreEqual(
                    size, display.width,
                    $"{mode}: 显示纹理宽度应等于网格宽，实际 {display.width}——打包布局泄漏到显示层了");
                Assert.AreEqual(size, display.height, $"{mode}: 显示纹理高度应等于网格高");

                Assert.AreEqual(
                    cpu.Snapshot().ComputeHash(),
                    ReadDisplayTexture(gpu).ComputeHash(),
                    $"{mode}: 显示出来的内容与逻辑状态不一致");
            }
            finally
            {
                gpu.Dispose();
            }
        }

        /// <summary>把"给人看的那张纹理"回读成快照——它应该是一像素一细胞。</summary>
        private static GridSnapshot ReadDisplayTexture(GpuGridKernel gpu)
        {
            RenderTexture texture = gpu.DisplayTexture;
            AsyncGPUReadbackRequest request =
                AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, "显示纹理回读失败");

            NativeArray<Color32> pixels = request.GetData<Color32>();
            int width = texture.width;
            int height = texture.height;

            var alive = new bool[width * height];
            int population = 0;
            for (int i = 0; i < alive.Length; i++)
            {
                bool isAlive = pixels[i].r > 127;
                alive[i] = isAlive;
                if (isAlive) population++;
            }

            return GridSnapshot.FromCells(width, height, alive, population, gpu.Generation);
        }

        [TestCase(GpuStepMode.Basic)]
        [TestCase(GpuStepMode.SharedMemory)]
        [TestCase(GpuStepMode.PackedChannels)]
        [TestCase(GpuStepMode.BitPacked)]
        public void Clear_ResetsBothPathsToEmpty(GpuStepMode mode)
        {
            RequireGpu();

            const int size = 32;
            var gpu = new GpuGridKernel(_shader, size, size, wrapEdges: true, stepMode: mode);

            try
            {
                gpu.StampPattern(PatternLibrary.Get("Glider"), 10, 10);
                gpu.Step();
                gpu.Clear();

                GridSnapshot snapshot = gpu.Snapshot();
                Assert.AreEqual(0, snapshot.Population, "Clear 后应为空");
                Assert.AreEqual(0, gpu.Generation, "Clear 后代数应归零");
            }
            finally
            {
                gpu.Dispose();
            }
        }

        [TestCase(GpuStepMode.Basic)]
        [TestCase(GpuStepMode.SharedMemory)]
        [TestCase(GpuStepMode.PackedChannels)]
        [TestCase(GpuStepMode.BitPacked)]
        public void Stamp_CountsExactPopulation(GpuStepMode mode)
        {
            RequireGpu();

            const int size = 32;
            var gpu = new GpuGridKernel(_shader, size, size, wrapEdges: true, stepMode: mode);

            try
            {
                PatternDefinition toad = PatternLibrary.Get("Toad");
                gpu.StampPattern(toad, 5, 5);

                Assert.AreEqual(toad.Cells.Count, gpu.Snapshot().Population, "盖印后人口数应等于构型细胞数");
            }
            finally
            {
                gpu.Dispose();
            }
        }

        /// <summary>单细胞构型，仅用于逐格盖印随机初始局面。</summary>
        private static PatternDefinition SingleCell { get; } = new PatternDefinition(
            "SingleCell", PatternCategory.StillLife, 1, new GridPos(0, 0), displayName: null, new GridPos(0, 0));

        private static void RequireGpu()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore(
                    "当前环境不支持 ComputeShader（如 -nographics 批处理）。" +
                    "GPU 一致性测试需要在有图形设备的模式下运行。");
            }
        }

        private static void AssertMatch(GridSnapshot cpu, GridSnapshot gpu, string context)
        {
            Assert.AreEqual(cpu.Population, gpu.Population, $"{context}：人口数不一致");
            Assert.AreEqual(
                cpu.ComputeHash(),
                gpu.ComputeHash(),
                $"{context}：状态哈希不一致\nCPU:\n{cpu.ToTrimmedAscii()}\nGPU:\n{gpu.ToTrimmedAscii()}");
        }
    }
}
