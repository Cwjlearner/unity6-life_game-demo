using System.Diagnostics;
using GameOfLife.Core;
using GameOfLife.Gpu;
using NUnit.Framework;
using UnityEditor;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameOfLife.Tests
{
    /// <summary>
    /// 基础版与档一（groupshared）的对比基准。
    /// 这个用例只断言"两种档位结果一致"，不对耗时做断言——耗时数据打印出来供人看与回填文档，
    /// 避免在 CI 负载波动时产生假失败。
    /// 测量口径：N 代连续 Dispatch，末尾用一次同步回读强制 GPU 执行完毕，
    /// 统计平均每代墙钟时间。包含提交开销，因此是相对比较而非绝对 GPU 耗时。
    /// </summary>
    [TestFixture]
    [Category("Benchmark")]
    public class StepBenchmarkTests
    {
        private const string ShaderPath = "Assets/Shaders/LifePingPong.compute";
        private const int Steps = 120;
        private const int WarmupSteps = 20;
        private const int Repetitions = 3;

        /// <summary>回归门槛：任何一档的耗时不得超过基础版的这个倍数。</summary>
        private const double RegressionTolerance = 1.15;

        private static readonly ProfilerMarker s_LoopMarker = new ProfilerMarker("GameOfLife.Bench.StepLoop");

        private static readonly int[] Sizes = { 256, 512, 1024, 2048 };

        private static readonly GpuStepMode[] Modes =
        {
            GpuStepMode.Basic,
            GpuStepMode.SharedMemory,
            GpuStepMode.PackedChannels,
            GpuStepMode.BitPacked,
        };

        private ComputeShader _shader;

        [OneTimeSetUp]
        public void LoadShader()
        {
            _shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            Assert.IsNotNull(_shader, $"未找到 ComputeShader：{ShaderPath}");
        }

        [Test]
        public void CompareStepModes_AndVerifyTheyAgree()
        {
            if (!SystemInfo.supportsComputeShaders)
                Assert.Ignore("当前环境不支持 ComputeShader，跳过基准测试。");

            TestContext.WriteLine("网格\t档位\t墙钟 ms/代\tCPU 提交 ms/代\t状态哈希");

            foreach (int size in Sizes)
            {
                var hashes = new System.Collections.Generic.Dictionary<GpuStepMode, ulong>();
                var wallTimes = new System.Collections.Generic.Dictionary<GpuStepMode, double>();

                foreach (GpuStepMode mode in Modes)
                {
                    (double wall, double submit, ulong hash) = Measure(size, mode);
                    hashes[mode] = hash;
                    wallTimes[mode] = wall;

                    string line =
                        $"{size}x{size}\t{mode}\t{wall:F4}\t{submit:F4}\t{hash:x16}";
                    TestContext.WriteLine(line);
                    Debug.Log($"[GoL-Bench] {line.Replace("\t", " | ")}");
                }

                // 四档必须给出完全相同的终态，否则"优化"已经改变了语义。
                foreach (GpuStepMode mode in Modes)
                {
                    Assert.AreEqual(
                        hashes[GpuStepMode.Basic],
                        hashes[mode],
                        $"{size}x{size}：{mode} 与基础版跑完 {Steps} 代后状态不一致，优化改变了结果语义");

                    // 回归门槛：任何一档都不该比基础版慢。四档在同一轮里测量，
                    // 机器负载对它们的影响是同向的，因此用相对比值做门槛比绝对耗时稳。
                    double ratio = wallTimes[mode] / wallTimes[GpuStepMode.Basic];
                    Assert.LessOrEqual(
                        ratio,
                        RegressionTolerance,
                        $"{size}x{size}：{mode} 耗时是基础版的 {ratio:F2} 倍（门槛 {RegressionTolerance:F2}），疑似性能回归");
                }
            }

            TestContext.WriteLine(
                $"步数={Steps}，预热={WarmupSteps}，重复={Repetitions}（取中位数）；" +
                "墙钟口径 = 连续 Dispatch + 末尾同步回读；CPU 提交口径 = ProfilerMarker 记录的 Dispatch 提交耗时");
        }

        /// <summary>重复多次取中位数：单次采样受机器负载影响明显，中位数比平均值更抗离群。</summary>
        private (double Wall, double Submit, ulong Hash) Measure(int size, GpuStepMode mode)
        {
            var samples = new (double Wall, double Submit, ulong Hash)[Repetitions];

            for (int repetition = 0; repetition < Repetitions; repetition++)
                samples[repetition] = MeasureOnce(size, mode);

            System.Array.Sort(samples, (a, b) => a.Wall.CompareTo(b.Wall));
            return samples[Repetitions / 2];
        }

        private (double Wall, double Submit, ulong Hash) MeasureOnce(int size, GpuStepMode mode)
        {
            var gpu = new GpuGridKernel(_shader, size, size, wrapEdges: true, stepMode: mode);

            try
            {
                StampDeterministicScene(gpu, size);

                // 预热：首次 Dispatch 会触发 compute shader 编译，必须排除在计时之外。
                for (int i = 0; i < WarmupSteps; i++)
                    gpu.Step();

                gpu.Snapshot();   // 强制等待 GPU 完成

                // 用 ProfilerMarker 记录纯 CPU 提交耗时，与包含 GPU 完成的墙钟时间对照：
                // 两者差距大说明瓶颈在 GPU 执行，接近说明瓶颈在提交侧。
                ProfilerRecorder recorder = ProfilerRecorder.StartNew(s_LoopMarker, 1);

                var stopwatch = Stopwatch.StartNew();
                using (s_LoopMarker.Auto())
                {
                    for (int i = 0; i < Steps; i++)
                        gpu.Step();
                }

                ulong hash = gpu.Snapshot().ComputeHash();   // 同步点，保证计到真实完成时间
                stopwatch.Stop();
                recorder.Stop();

                double submitMs = recorder.Valid && recorder.Count > 0
                    ? recorder.LastValue / 1_000_000.0 / Steps
                    : double.NaN;

                return (stopwatch.Elapsed.TotalMilliseconds / Steps, submitMs, hash);
            }
            finally
            {
                gpu.Dispose();
            }
        }

        /// <summary>盖上 6 个构型，保证两种档位跑的是同一局面。</summary>
        private static void StampDeterministicScene(GpuGridKernel gpu, int size)
        {
            int quarter = size / 4;

            gpu.StampPattern(PatternLibrary.Get("Glider"), quarter, quarter);
            gpu.StampPattern(PatternLibrary.Get("LWSS"), quarter * 3, quarter);
            gpu.StampPattern(PatternLibrary.Get("Blinker"), quarter, quarter * 3);
            gpu.StampPattern(PatternLibrary.Get("Toad"), quarter * 3, quarter * 3);
            gpu.StampPattern(PatternLibrary.Get("Block"), size / 2, size / 2);
            gpu.StampPattern(PatternLibrary.Get("Beehive"), size / 2 + 8, size / 2 + 8);
        }
    }
}
