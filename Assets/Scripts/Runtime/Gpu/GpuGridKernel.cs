using System;
using System.Collections.Generic;
using GameOfLife.Core;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace GameOfLife.Gpu
{
    /// <summary>
    /// ComputeShader 驱动的演化内核：状态存在两张同规格 RenderTexture 中乒乓交替。
    /// 关键约束：
    /// 1. 同一次 Dispatch 内不能对同一纹理既读又写，因此必须有 _current / _target 两张纹理；
    /// 2. 线程组按 8 取整，kernel 内已做越界守卫，主机侧线程组数按 ceil 计算；
    /// 3. 纹理必须在创建时就设好 enableRandomWrite，运行时无法更改。
    /// Population 语义：GPU 上无法在不回读的前提下统计活细胞数，
    /// 因此该值返回"最近一次快照测得的人口"，精确值请用 Snapshot。
    /// </summary>
    public sealed class GpuGridKernel : IGridKernel, IDisposable
    {
        private const int ClearThreadGroupSize = 8;    // Clear  kernel: numthreads(8,8,1)
        private const int BasicThreadGroupSize = 8;    // Step   kernel: numthreads(8,8,1)
        private const int SharedThreadGroupSize = 16;  // StepShared: numthreads(TILE,TILE,1)，TILE = 16
        private const int PackedThreadGroupSize = 8;   // StepPacked: numthreads(8,8,1)
        private const int StampThreadGroupSize = 64;

        // 用 ProfilerMarker 而不是 Deep Profile：前者开销可忽略且能长期留在代码里，
        // 后者会让耗时失真 10~100 倍，不能用来做性能判断。
        private static readonly ProfilerMarker s_StepMarker = new ProfilerMarker("GameOfLife.Gpu.Step");
        private static readonly ProfilerMarker s_ExpandMarker = new ProfilerMarker("GameOfLife.Gpu.Expand");
        private static readonly ProfilerMarker s_ReadbackMarker = new ProfilerMarker("GameOfLife.Gpu.Readback");

        private static readonly int s_State = Shader.PropertyToID("_State");
        private static readonly int s_Source = Shader.PropertyToID("_Source");
        private static readonly int s_Result = Shader.PropertyToID("_Result");
        private static readonly int s_Cells = Shader.PropertyToID("_Cells");
        private static readonly int s_Width = Shader.PropertyToID("_Width");
        private static readonly int s_Height = Shader.PropertyToID("_Height");
        private static readonly int s_CellCount = Shader.PropertyToID("_CellCount");
        private static readonly int s_Wrap = Shader.PropertyToID("_Wrap");
        private static readonly int s_TextureWidth = Shader.PropertyToID("_TextureWidth");
        private static readonly int s_StampEntries = Shader.PropertyToID("_StampEntries");
        private static readonly int s_EntryCount = Shader.PropertyToID("_EntryCount");
        private static readonly int s_WordsPerRow = Shader.PropertyToID("_WordsPerRow");
        private static readonly int s_StatePacked = Shader.PropertyToID("_StatePacked");
        private static readonly int s_SourcePacked = Shader.PropertyToID("_SourcePacked");
        private static readonly int s_ResultPacked = Shader.PropertyToID("_ResultPacked");
        private static readonly int s_Expanded = Shader.PropertyToID("_Expanded");
        private static readonly int s_Seed = Shader.PropertyToID("_Seed");
        private static readonly int s_AliveThreshold = Shader.PropertyToID("_AliveThreshold");

        private readonly ComputeShader _shader;
        private readonly int _kernelClear;
        private readonly int _kernelStamp;
        private readonly int _kernelStep;
        private readonly int _kernelExpand;
        private readonly int _kernelFill;
        private readonly int _stepThreadGroupSize;
        private readonly ComputeBuffer _cellBuffer;
        private readonly Vector2Int[] _cellScratch;
        private readonly ComputeBuffer _texelBuffer;
        private readonly Vector2Int[] _texelScratch;

        /// <summary>状态纹理的宽（texel 数）。档二下等于网格宽 / 4。</summary>
        private readonly int _textureWidth;

        private RenderTexture _current;
        private RenderTexture _target;

        /// <summary>档三专用：位打包状态展开成 1 texel = 1 细胞的纹理，供显示与回读使用。</summary>
        private RenderTexture _expanded;
        private int _expandedGeneration = -1;
        private bool _disposed;

        public int Width { get; }
        public int Height { get; }
        public bool WrapEdges { get; }
        public int Generation { get; private set; }

        /// <summary>最近一次快照测得的活细胞数；未快照过时为 0。</summary>
        public int Population { get; private set; }

        /// <summary>当前代数对应的状态纹理（乒乓交换后会变，因此每次取都要重新读）。</summary>
        public RenderTexture CurrentTexture => _current;

        /// <summary>当前使用的演进 kernel 档位。</summary>
        public GpuStepMode StepMode { get; }

        public GpuGridKernel(
            ComputeShader shader,
            int width,
            int height,
            bool wrapEdges = true,
            GpuStepMode stepMode = GpuStepMode.Basic,
            int maxStampCells = 4096)
        {
            if (shader == null) throw new ArgumentNullException(nameof(shader));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "宽度必须为正");
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "高度必须为正");
            if (maxStampCells <= 0) throw new ArgumentOutOfRangeException(nameof(maxStampCells));
            if (stepMode == GpuStepMode.PackedChannels && width % 4 != 0)
            {
                throw new ArgumentException(
                    $"档二（四通道打包）要求网格宽度是 4 的倍数，当前宽度为 {width}。" +
                    "宽度不是 4 的倍数时尾部会出现填充通道，按细胞回绕与按 texel 回绕不再等价。",
                    nameof(width));
            }
            if (stepMode == GpuStepMode.BitPacked && width % 32 != 0)
            {
                throw new ArgumentException(
                    $"档三（位打包）要求网格宽度是 32 的倍数，当前宽度为 {width}。" +
                    "宽度不是 32 的倍数时每行最后一个字会出现无效位，跨字移位与回绕的边界会偏离一位。",
                    nameof(width));
            }

            _shader = shader;
            Width = width;
            Height = height;
            WrapEdges = wrapEdges;
            StepMode = stepMode;
            _textureWidth = stepMode switch
            {
                GpuStepMode.PackedChannels => (width + 3) / 4,
                GpuStepMode.BitPacked => (width + 31) / 32,
                _ => width,
            };

            bool bitPacked = stepMode == GpuStepMode.BitPacked;

            _kernelClear = RequireKernel(bitPacked ? "ClearPacked" : "Clear");
            _kernelStamp = RequireKernel(stepMode switch
            {
                GpuStepMode.PackedChannels => "StampPacked",
                GpuStepMode.BitPacked => "StampBitPacked",
                _ => "Stamp",
            });
            _kernelStep = RequireKernel(stepMode switch
            {
                GpuStepMode.SharedMemory => "StepShared",
                GpuStepMode.PackedChannels => "StepPacked",
                GpuStepMode.BitPacked => "StepBitPacked",
                _ => "Step",
            });
            _kernelExpand = RequireKernel("ExpandBitPacked");
            _kernelFill = RequireKernel(stepMode switch
            {
                GpuStepMode.PackedChannels => "FillRandomPacked",
                GpuStepMode.BitPacked => "FillRandomBitPacked",
                _ => "FillRandom",
            });
            _stepThreadGroupSize = stepMode switch
            {
                GpuStepMode.SharedMemory => SharedThreadGroupSize,
                GpuStepMode.PackedChannels => PackedThreadGroupSize,
                GpuStepMode.BitPacked => PackedThreadGroupSize,
                _ => BasicThreadGroupSize,
            };

            _current = CreateStateTexture("GoL_Current");
            _target = CreateStateTexture("GoL_Target");
            _expanded = bitPacked ? CreateExpandedTexture() : null;

            _cellBuffer = new ComputeBuffer(maxStampCells, sizeof(int) * 2, ComputeBufferType.Structured);
            _cellScratch = new Vector2Int[maxStampCells];
            _texelBuffer = new ComputeBuffer(maxStampCells, sizeof(int) * 2, ComputeBufferType.Structured);
            _texelScratch = new Vector2Int[maxStampCells];

            Clear();
        }

        public void Clear()
        {
            ThrowIfDisposed();

            // Clear 作用于状态纹理本身，因此尺寸用纹理尺寸（档二、档三与网格尺寸不同）。
            if (StepMode == GpuStepMode.BitPacked)
            {
                _shader.SetInt(s_WordsPerRow, _textureWidth);
                _shader.SetInt(s_Height, Height);
                _shader.SetTexture(_kernelClear, s_StatePacked, _current);
            }
            else
            {
                _shader.SetInt(s_Width, _textureWidth);
                _shader.SetInt(s_Height, Height);
                _shader.SetTexture(_kernelClear, s_State, _current);
            }

            _shader.Dispatch(
                _kernelClear,
                GroupCount(_textureWidth, ClearThreadGroupSize),
                GroupCount(Height, ClearThreadGroupSize),
                1);

            Generation = 0;
            Population = 0;
            _expandedGeneration = -1;
        }

        /// <summary>
        /// 按种子填充随机初始状态。使用与 CPU 侧完全相同的整数哈希（见 RandomField），
        /// 因此同一个 seed 在两条路径上会得到逐位相同的棋盘——这条由 GPU 一致性测试把关。
        /// </summary>
        public void FillRandom(int seed, float aliveProbability = 0.32f)
        {
            ThrowIfDisposed();

            uint threshold = RandomField.Threshold(aliveProbability);

            _shader.SetInt(s_Seed, seed);
            _shader.SetInt(s_AliveThreshold, unchecked((int)threshold));
            _shader.SetInt(s_Width, Width);
            _shader.SetInt(s_Height, Height);
            _shader.SetInt(s_TextureWidth, _textureWidth);
            _shader.SetInt(s_WordsPerRow, _textureWidth);

            if (StepMode == GpuStepMode.BitPacked)
                _shader.SetTexture(_kernelFill, s_StatePacked, _current);
            else
                _shader.SetTexture(_kernelFill, s_State, _current);

            _shader.Dispatch(
                _kernelFill,
                GroupCount(_textureWidth, ClearThreadGroupSize),
                GroupCount(Height, ClearThreadGroupSize),
                1);

            Generation = 0;
            Population = 0;
            _expandedGeneration = -1;
        }

        public void StampPattern(PatternDefinition pattern, int originX, int originY)
        {
            ThrowIfDisposed();
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));

            // 与 CPU 路径共用坐标合成，且同样对越界抛异常（不静默裁剪）。
            PatternStamp.ValidateInside(pattern, originX, originY, Width, Height);
            GridPos[] cells = PatternStamp.ComposeAbsolute(pattern, originX, originY);

            if (cells.Length > _cellBuffer.count)
            {
                throw new ArgumentException(
                    $"{pattern.Name} 有 {cells.Length} 个细胞，超过盖印缓冲容量 {_cellBuffer.count}");
            }

            if (StepMode == GpuStepMode.PackedChannels)
            {
                StampGroupedCells(pattern.Name, cells, shift: 2);   // 4 个细胞 / texel
            }
            else if (StepMode == GpuStepMode.BitPacked)
            {
                StampGroupedCells(pattern.Name, cells, shift: 5);   // 32 个细胞 / uint
            }
            else
            {
                for (int i = 0; i < cells.Length; i++)
                    _cellScratch[i] = new Vector2Int(cells[i].X, cells[i].Y);

                _cellBuffer.SetData(_cellScratch, 0, 0, cells.Length);

                _shader.SetInt(s_Width, Width);
                _shader.SetInt(s_Height, Height);
                _shader.SetInt(s_CellCount, cells.Length);
                _shader.SetBuffer(_kernelStamp, s_Cells, _cellBuffer);
                _shader.SetTexture(_kernelStamp, s_State, _current);
                _shader.Dispatch(_kernelStamp, DivideRoundUp(cells.Length, StampThreadGroupSize), 1, 1);
            }

            // 盖印后人口数未知（可能覆盖已有活细胞），交给下一次快照刷新。
            Population = 0;
            _expandedGeneration = -1;
        }

        /// <summary>
        /// 档二与档三共用的盖印：先按"承载单元"聚合位掩码，再让每个线程只写自己那一个单元。
        /// 直接按细胞并发写会命中同一单元的读改写竞争（比如横向音符 blinker 的三个细胞
        /// 落在同一个 texel 的三个通道上），聚合后每个单元在列表里只出现一次，竞争自然消失。
        /// shift = 2 表示一个 texel 承载 4 个细胞（档二），shift = 5 表示一个 uint 承载 32 个（档三）。
        /// </summary>
        private void StampGroupedCells(string patternName, GridPos[] cells, int shift)
        {
            int cellsPerUnit = 1 << shift;
            int cellMask = cellsPerUnit - 1;
            var entries = new Dictionary<int, int>();

            foreach (GridPos cell in cells)
            {
                int unitIndex = cell.Y * _textureWidth + (cell.X >> shift);
                int channelBit = 1 << (cell.X & cellMask);

                entries.TryGetValue(unitIndex, out int mask);
                entries[unitIndex] = mask | channelBit;
            }

            if (entries.Count > _texelBuffer.count)
            {
                throw new ArgumentException(
                    $"{patternName} 聚合后需要写入 {entries.Count} 个单元，超过盖印缓冲容量 {_texelBuffer.count}");
            }

            int index = 0;
            foreach (KeyValuePair<int, int> entry in entries)
                _texelScratch[index++] = new Vector2Int(entry.Key, entry.Value);

            _texelBuffer.SetData(_texelScratch, 0, 0, entries.Count);

            _shader.SetInt(s_TextureWidth, _textureWidth);
            _shader.SetInt(s_WordsPerRow, _textureWidth);
            _shader.SetInt(s_Height, Height);
            _shader.SetInt(s_EntryCount, entries.Count);
            _shader.SetBuffer(_kernelStamp, s_StampEntries, _texelBuffer);

            // 档三的状态纹理是整型单通道，必须绑到 _StatePacked 上；
            // 绑错名字不会报错，只会安静地什么都不写——表现为人口数恒为 0。
            if (StepMode == GpuStepMode.BitPacked)
                _shader.SetTexture(_kernelStamp, s_StatePacked, _current);
            else
                _shader.SetTexture(_kernelStamp, s_State, _current);

            _shader.Dispatch(_kernelStamp, DivideRoundUp(entries.Count, StampThreadGroupSize), 1, 1);
        }

        public void Step()
        {
            ThrowIfDisposed();

            using (s_StepMarker.Auto())
            {
                _shader.SetInt(s_Width, Width);
                _shader.SetInt(s_Height, Height);
                _shader.SetInt(s_TextureWidth, _textureWidth);
                _shader.SetInt(s_WordsPerRow, _textureWidth);
                _shader.SetFloat(s_Wrap, WrapEdges ? 1f : 0f);

                if (StepMode == GpuStepMode.BitPacked)
                {
                    _shader.SetTexture(_kernelStep, s_SourcePacked, _current);
                    _shader.SetTexture(_kernelStep, s_ResultPacked, _target);
                }
                else
                {
                    _shader.SetTexture(_kernelStep, s_Source, _current);
                    _shader.SetTexture(_kernelStep, s_Result, _target);
                }

                _shader.Dispatch(
                    _kernelStep,
                    GroupCount(_textureWidth, _stepThreadGroupSize),
                    GroupCount(Height, _stepThreadGroupSize),
                    1);
            }

            (_current, _target) = (_target, _current);
            Generation++;
            _expandedGeneration = -1;
        }

        /// <summary>
        /// 同步回读快照。内部会阻塞等待 GPU 完成，仅用于验证、测试与低频统计，
        /// 不要放在每帧热路径上。
        /// </summary>
        public GridSnapshot Snapshot()
        {
            ThrowIfDisposed();

            using (s_ReadbackMarker.Auto())
            {
                AsyncGPUReadbackRequest request =
                    AsyncGPUReadback.Request(ReadbackSource, 0, TextureFormat.RGBA32);
                request.WaitForCompletion();

                if (request.hasError)
                    throw new InvalidOperationException("GPU 回读失败（AsyncGPUReadback.hasError）");

                NativeArray<Color32> pixels = request.GetData<Color32>();
                return StepMode == GpuStepMode.PackedChannels
                    ? ConvertPackedSnapshot(pixels, Generation)
                    : ConvertToSnapshot(pixels, Generation);
            }
        }

        /// <summary>
        /// 异步回读，供界面低频刷新人口数等统计信息使用。
        /// </summary>
        public void RequestSnapshot(Action<GridSnapshot> onCompleted)
        {
            ThrowIfDisposed();
            if (onCompleted == null) throw new ArgumentNullException(nameof(onCompleted));

            int generationAtRequest = Generation;
            RenderTexture source = ReadbackSource;

            AsyncGPUReadback.Request(source, 0, TextureFormat.RGBA32, request =>
            {
                if (request.hasError || _disposed) return;

                NativeArray<Color32> pixels = request.GetData<Color32>();
                onCompleted(StepMode == GpuStepMode.PackedChannels
                    ? ConvertPackedSnapshot(pixels, generationAtRequest)
                    : ConvertToSnapshot(pixels, generationAtRequest));
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cellBuffer.Release();
            _texelBuffer.Release();
            ReleaseTexture(ref _current);
            ReleaseTexture(ref _target);
            ReleaseTexture(ref _expanded);
        }

        /// <summary>
        /// 显示用纹理：档三的位打包纹理不能直接显示，需要先展开成 1 texel = 1 细胞。
        /// 展开按需进行（每代最多一次），其他档位直接返回状态纹理本身。
        /// </summary>
        public RenderTexture DisplayTexture
        {
            get
            {
                ThrowIfDisposed();
                ExpandIfNeeded();
                return StepMode == GpuStepMode.BitPacked ? _expanded : _current;
            }
        }

        /// <summary>回读前必须确保展开纹理与当前代一致。</summary>
        private RenderTexture ReadbackSource
        {
            get
            {
                ExpandIfNeeded(force: true);
                return StepMode == GpuStepMode.BitPacked ? _expanded : _current;
            }
        }

        private void ExpandIfNeeded(bool force = false)
        {
            if (StepMode != GpuStepMode.BitPacked || _expanded == null) return;
            if (!force && _expandedGeneration == Generation) return;

            using (s_ExpandMarker.Auto())
            {
                _shader.SetInt(s_Width, Width);
                _shader.SetInt(s_Height, Height);
                _shader.SetTexture(_kernelExpand, s_SourcePacked, _current);
                _shader.SetTexture(_kernelExpand, s_Expanded, _expanded);
                _shader.Dispatch(
                    _kernelExpand,
                    GroupCount(Width, ClearThreadGroupSize),
                    GroupCount(Height, ClearThreadGroupSize),
                    1);
            }

            _expandedGeneration = Generation;
        }

        private GridSnapshot ConvertToSnapshot(NativeArray<Color32> pixels, int generation)
        {
            int pixelCount = Width * Height;
            var alive = new bool[pixelCount];
            int population = 0;

            for (int i = 0; i < pixelCount; i++)
            {
                // texel(x,y) 与网格(x,y) 一一对应（见 compute 头部约定），
                // 因此像素线性下标 i 就是网格线性下标 y * Width + x。
                bool isAlive = pixels[i].r > 127;
                alive[i] = isAlive;
                if (isAlive) population++;
            }

            Population = population;
            return GridSnapshot.FromCells(Width, Height, alive, population, generation);
        }

        /// <summary>档二回读：一个 texel 的四个通道各是一个细胞，需要拆回网格坐标。</summary>
        private GridSnapshot ConvertPackedSnapshot(NativeArray<Color32> pixels, int generation)
        {
            var alive = new bool[Width * Height];
            int population = 0;

            for (int y = 0; y < Height; y++)
            {
                int row = y * Width;
                int texelRow = y * _textureWidth;

                for (int texelX = 0; texelX < _textureWidth; texelX++)
                {
                    Color32 texel = pixels[texelRow + texelX];
                    int baseX = texelX * 4;

                    if (texel.r > 127 && baseX < Width) { alive[row + baseX] = true; population++; }
                    if (texel.g > 127 && baseX + 1 < Width) { alive[row + baseX + 1] = true; population++; }
                    if (texel.b > 127 && baseX + 2 < Width) { alive[row + baseX + 2] = true; population++; }
                    if (texel.a > 127 && baseX + 3 < Width) { alive[row + baseX + 3] = true; population++; }
                }
            }

            Population = population;
            return GridSnapshot.FromCells(Width, Height, alive, population, generation);
        }

        private RenderTexture CreateStateTexture(string name)
        {
            // 尺寸用纹理尺寸而不是网格尺寸：档二下宽度是网格的 1/4，档三下是 1/32。
            var descriptor = new RenderTextureDescriptor(_textureWidth, Height, RenderTextureFormat.ARGB32, 0)
            {
                enableRandomWrite = true,   // 创建后不可更改，必须在这里定好
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
            };

            // 档三用整型单通道承载 32 个细胞，必须是 uint 格式才能被 RWTexture2D<uint> 绑定。
            if (StepMode == GpuStepMode.BitPacked)
                descriptor.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32_UInt;

            var texture = new RenderTexture(descriptor)
            {
                name = name,
                filterMode = FilterMode.Point,      // 细胞是离散的，禁止插值
                wrapMode = TextureWrapMode.Repeat,  // 边界语义由 kernel 决定
            };

            if (!texture.Create())
                throw new InvalidOperationException($"创建 RenderTexture 失败：{name} ({Width}x{Height})");

            return texture;
        }

        private RenderTexture CreateExpandedTexture()
        {
            var descriptor = new RenderTextureDescriptor(Width, Height, RenderTextureFormat.ARGB32, 0)
            {
                enableRandomWrite = true,
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
            };

            var texture = new RenderTexture(descriptor)
            {
                name = "GoL_Expanded",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            if (!texture.Create())
                throw new InvalidOperationException($"创建展开纹理失败：{Width}x{Height}");

            return texture;
        }

        private int RequireKernel(string kernelName)
        {
            if (!_shader.HasKernel(kernelName))
                throw new ArgumentException($"ComputeShader 缺少 kernel：{kernelName}", nameof(_shader));

            return _shader.FindKernel(kernelName);
        }

        private static int GroupCount(int size, int threadGroupSize) =>
            DivideRoundUp(size, threadGroupSize);

        private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

        private static void ReleaseTexture(ref RenderTexture texture)
        {
            if (texture == null) return;

            texture.Release();
            UnityEngine.Object.DestroyImmediate(texture);
            texture = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GpuGridKernel));
        }
    }
}
