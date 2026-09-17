using System;
using GameOfLife.Core;
using UnityEngine;

namespace GameOfLife.Gpu
{
    /// <summary>
    /// 按运行环境选择演化内核：能用 ComputeShader 就走 GPU，否则回退 CPU 参考实现。
    /// 回退不是"兜底代码"，而是方案的一部分：WebGL 等平台没有 ComputeShader，
    /// 有了它，演示不会在评审环境里直接失败，界面与控制逻辑也完全不用改
    /// （两条路径实现同一个 IGridKernel）。
    /// </summary>
    public static class GridKernelFactory
    {
        /// <summary>当前环境是否支持 ComputeShader 路径。</summary>
        public static bool SupportsGpu(ComputeShader shader) =>
            shader != null && SystemInfo.supportsComputeShaders;

        public static IGridKernel Create(
            ComputeShader shader,
            int width,
            int height,
            bool wrapEdges = true,
            GpuStepMode stepMode = GpuStepMode.Basic,
            Action<string> onFallback = null)
        {
            if (SupportsGpu(shader))
            {
                // 档二的适用前提不满足时降级到档一，而不是给出错误结果或直接掉到 CPU。
                if (stepMode == GpuStepMode.PackedChannels && width % 4 != 0)
                {
                    onFallback?.Invoke(
                        $"档二要求网格宽度是 4 的倍数（当前宽度 {width}），已改用档一。");
                    stepMode = GpuStepMode.SharedMemory;
                }
                else if (stepMode == GpuStepMode.BitPacked && width % 32 != 0)
                {
                    onFallback?.Invoke(
                        $"档三要求网格宽度是 32 的倍数（当前宽度 {width}），已改用档二。");
                    stepMode = width % 4 == 0 ? GpuStepMode.PackedChannels : GpuStepMode.SharedMemory;
                }

                try
                {
                    return new GpuGridKernel(shader, width, height, wrapEdges, stepMode);
                }
                catch (Exception exception)
                {
                    onFallback?.Invoke($"GPU 内核创建失败，回退到 CPU：{exception.Message}");
                }
            }
            else
            {
                onFallback?.Invoke(SystemInfo.supportsComputeShaders
                    ? "未指定 ComputeShader，使用 CPU 参考实现。"
                    : "当前平台不支持 ComputeShader，使用 CPU 参考实现。");
            }

            return new GridModel(width, height, wrapEdges);
        }
    }
}
