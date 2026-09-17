using System;
using System.Collections.Generic;
using GameOfLife.Core;
using GameOfLife.Gpu;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace GameOfLife.Presentation
{
    /// <summary>
    /// 演示控制层：内核的创建与生命周期、演进节奏（启停/单步/调速）、
    /// 构型切换与重置，并把当前状态纹理交给显示层。
    /// 它不关心内核是 CPU 还是 GPU 实现——两者都通过 IGridKernel 使用。
    /// </summary>
    [AddComponentMenu("Game Of Life/Life Controller")]
    public sealed class LifeController : MonoBehaviour
    {
        [Header("模拟")]
        [SerializeField] private ComputeShader _computeShader;
        // 默认 64×64：构型只有几格宽，网格越大单个细胞在屏幕上越小。
        // 想看随机汤或跑性能对比时再调大（256 / 512 / 1024 都满足档二与档三的宽度约束）。
        [SerializeField] private int _width = 64;
        [SerializeField] private int _height = 64;
        [SerializeField] private bool _wrapEdges = true;
        // 演示默认用档二：只要求宽度是 4 的倍数（覆盖面比档三宽），
        // 速度与档三只差约 10%，且显示路径不需要额外的展开通道。
        [SerializeField] private GpuStepMode _stepMode = GpuStepMode.PackedChannels;

        [Header("播放")]
        [SerializeField] private bool _playOnStart = true;
        [SerializeField] private float _generationsPerSecond = 12f;
        [SerializeField] private int _maxStepsPerFrame = 8;

        [Header("构型")]
        [Tooltip("重置与进入 Play 时，初始状态从哪里来。想跑随机模拟就选后两项。")]
        [SerializeField] private InitialStateSource _initialSource = InitialStateSource.PresetPattern;

        [SerializeField] private int _patternIndex = 4;

        [Header("随机初始状态")]
        [Tooltip("同一个种子在 CPU 与 GPU 上都会得到完全相同的棋盘，可复现、可比对。")]
        [SerializeField] private int _seed = 20260917;

        [Tooltip("初始存活密度。0.3 左右最容易演化出丰富的结构。")]
        [Range(0.05f, 0.95f)]
        [SerializeField] private float _aliveProbability = 0.32f;

        [Tooltip("演示用快捷键：空格 播放/暂停，N 下一个构型，R 重置，方向键→ 单步。")]
        [SerializeField] private bool _enableKeyboardShortcuts = true;

        private float _accumulator;
        private Texture2D _cpuTexture;
        private Color32[] _cpuPixels;
        private int _cpuTextureGeneration = -1;

        /// <summary>当前演化内核；未初始化时为 null。</summary>
        public IGridKernel Kernel { get; private set; }

        public bool IsPlaying { get; private set; }
        public int Width => _width;
        public int Height => _height;
        public int PatternIndex => _patternIndex;
        public InitialStateSource InitialSource => _initialSource;
        public int Seed => _seed;
        public float AliveProbability => _aliveProbability;
        public PatternDefinition CurrentPattern => PatternLibrary.Get(_patternIndex);
        public float GenerationsPerSecond => _generationsPerSecond;
        public int Generation => Kernel?.Generation ?? 0;
        public int Population => Kernel?.Population ?? 0;
        public string FallbackMessage { get; private set; }

        /// <summary>最近一次分析检出的周期；0 表示未检出。</summary>
        public int DetectedPeriod { get; private set; }

        /// <summary>最近一次分析检出的每周期位移。</summary>
        public GridPos DetectedDisplacement { get; private set; }

        /// <summary>分析结论的人类可读描述（含与构型声明的一致性），供编辑器面板展示。</summary>
        public string AnalysisMessage { get; private set; }

        /// <summary>当前状态纹理：GPU 路径直接暴露 RenderTexture，CPU 路径维护一张等价 Texture2D。</summary>
        public Texture DisplayTexture
        {
            get
            {
                if (Kernel == null) return null;
                return Kernel is GpuGridKernel gpu ? gpu.DisplayTexture : _cpuTexture;
            }
        }

        private void OnEnable()
        {
            if (Application.isPlaying)
                Rebuild();
        }

        private void OnDisable()
        {
            DisposeKernel();
            DestroyCpuTexture();
        }

        private void OnValidate()
        {
            _width = Mathf.Max(8, _width);
            _height = Mathf.Max(8, _height);
            _generationsPerSecond = Mathf.Max(0.1f, _generationsPerSecond);
            _maxStepsPerFrame = Mathf.Max(1, _maxStepsPerFrame);
            _patternIndex = Mathf.Clamp(_patternIndex, 0, PatternLibrary.Count - 1);
        }

        private void Update()
        {
            if (Kernel == null) return;

            if (_enableKeyboardShortcuts)
                HandleShortcuts();

            if (IsPlaying)
                Advance(Time.unscaledDeltaTime);

            RefreshCpuTextureIfNeeded();
        }

        /// <summary>
        /// 演示快捷键。本项目使用新版 Input System（activeInputHandler = 1），
        /// 旧版 UnityEngine.Input 会直接抛异常，因此这里走 Input System API；
        /// 同时保留空引用守卫，换成旧版输入时也不会报错。
        /// </summary>
        private void HandleShortcuts()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.spaceKey.wasPressedThisFrame) TogglePlay();
            if (keyboard.nKey.wasPressedThisFrame) NextPattern();
            if (keyboard.rKey.wasPressedThisFrame) ResetToInitial();
            if (keyboard.rightArrowKey.wasPressedThisFrame) StepOnce();
#endif
        }

        /// <summary>（重新）创建内核并复位到当前构型。</summary>
        public void Rebuild()
        {
            DisposeKernel();
            DestroyCpuTexture();

            FallbackMessage = null;
            Kernel = GridKernelFactory.Create(
                _computeShader,
                _width,
                _height,
                _wrapEdges,
                stepMode: _stepMode,
                onFallback: message => FallbackMessage = message);

            ResetToInitial();
            IsPlaying = _playOnStart;
            _accumulator = 0f;
        }

        /// <summary>
        /// 按当前设定的初始状态来源重置棋盘。这是"重置"按钮与 `R` 键的统一入口，
        /// 也是进入 Play 时的初始化动作：想跑随机模拟就把来源设成随机，不必改代码。
        /// </summary>
        public void ResetToInitial()
        {
            switch (_initialSource)
            {
                case InitialStateSource.RandomWithSeed:
                    ResetToRandom();
                    break;

                case InitialStateSource.RandomEachTime:
                    RandomizeSeed();
                    break;

                default:
                    ResetToPattern();
                    break;
            }
        }

        public void SetStepMode(GpuStepMode mode)
        {
            _stepMode = mode;
            Rebuild();
        }

        public void SetWrapEdges(bool wrapEdges)
        {
            _wrapEdges = wrapEdges;
            Rebuild();
        }

        public void SetGridSize(int width, int height)
        {
            _width = Mathf.Max(8, width);
            _height = Mathf.Max(8, height);
            Rebuild();
        }

        /// <summary>
        /// 分析当前局面：把 GPU 状态取回 CPU 参考实现，向前演化若干代，
        /// 用与测试完全相同的判定逻辑算出周期与每周期位移。
        /// 目的是把"稳定 / 振荡 / 循环震荡"从观感变成可读的数字，
        /// 并顺带验证当前局面与构型声明是否一致。
        /// </summary>
        public void AnalyzeCurrentPattern(int maxPeriod = 8, int maxDisplacement = 4)
        {
            EnsureKernel();

            var model = new GridModel(_width, _height, _wrapEdges);
            model.LoadFrom(Kernel.Snapshot());

            var history = new List<GridSnapshot> { model.Snapshot() };
            int generations = Mathf.Max(maxPeriod * 2, 4);
            for (int i = 0; i < generations; i++)
            {
                model.Step();
                history.Add(model.Snapshot());
            }

            if (!PatternCompare.TryDetectPeriod(history, maxPeriod, maxDisplacement,
                    out int period, out GridPos displacement))
            {
                DetectedPeriod = 0;
                DetectedDisplacement = new GridPos(0, 0);
                AnalysisMessage = $"未在 {generations} 代内检出周期（当前局面可能不是单一构型）";
                return;
            }

            DetectedPeriod = period;
            DetectedDisplacement = displacement;

            string kind = period == 1
                ? "稳定静物"
                : displacement.X != 0 || displacement.Y != 0
                    ? "循环震荡（飞船）"
                    : "振荡器";

            string shift = displacement.X != 0 || displacement.Y != 0
                ? $"，每周期位移 ({displacement.X},{displacement.Y})"
                : string.Empty;

            PatternDefinition declared = CurrentPattern;
            bool matchesDeclared =
                declared.Period == period
                && declared.Displacement.X == displacement.X
                && declared.Displacement.Y == displacement.Y;

            AnalysisMessage =
                $"{kind}：周期 {period}{shift}（{declared.Name} 声明周期 {declared.Period}" +
                $"，位移 ({declared.Displacement.X},{declared.Displacement.Y})）" +
                (matchesDeclared ? " ✓ 一致" : " ⚠ 与声明不一致");
        }

        /// <summary>确保内核已创建（供编辑器工具等非 Play 场景调用）。</summary>
        public void EnsureKernel()
        {
            if (Kernel == null) Rebuild();
        }

        public void Play()
        {
            EnsureKernel();
            IsPlaying = true;
        }

        public void Pause() => IsPlaying = false;

        public void TogglePlay()
        {
            if (IsPlaying) Pause();
            else Play();
        }

        /// <summary>单步演化一代（会自动暂停，符合逐步检查的使用直觉）。</summary>
        public void StepOnce()
        {
            EnsureKernel();
            IsPlaying = false;
            Kernel.Step();
            RefreshCpuTextureIfNeeded(force: true);
        }

        /// <summary>清空并重新盖印当前构型（构型居中）。</summary>
        public void ResetToPattern()
        {
            EnsureKernel();

            Kernel.Clear();
            GridPos origin = PatternStamp.CenterOrigin(CurrentPattern, _width, _height);
            Kernel.StampPattern(CurrentPattern, origin.X, origin.Y);

            _accumulator = 0f;
            RefreshCpuTextureIfNeeded(force: true);

            // 刚重置时局面是"纯构型"，此刻分析最有意义，顺手把周期与位移算出来。
            AnalyzeCurrentPattern();
        }

        public void SelectPattern(int index)
        {
            _patternIndex = Mathf.Clamp(index, 0, PatternLibrary.Count - 1);

            // 主动选构型意味着"我要看预制构型"，因此把来源切回预制构型，
            // 否则选了构型却看不到任何变化，会让人困惑。
            _initialSource = InitialStateSource.PresetPattern;
            ResetToPattern();
        }

        /// <summary>
        /// 按当前种子填充随机初始状态。随机汤不构成单一构型，
        /// 因此这里不做周期分析，而是给出种子、密度与活细胞数，剩下的交给观察。
        /// </summary>
        public void ResetToRandom()
        {
            EnsureKernel();

            Kernel.FillRandom(_seed, _aliveProbability);
            _accumulator = 0f;
            RefreshCpuTextureIfNeeded(force: true);

            // GPU 路径的 Population 只是"最近一次快照"的值，填充后尚未测量，
            // 所以这里主动取一次快照拿到准确数字（用户触发一次，回读代价可接受）。
            GridSnapshot snapshot = Kernel.Snapshot();

            DetectedPeriod = 0;
            DetectedDisplacement = new GridPos(0, 0);
            AnalysisMessage =
                $"随机初始状态：种子 {_seed}，密度 {_aliveProbability:P0}，活细胞 {snapshot.Population} 个。" +
                "随机汤不是单一构型，不会有固定周期——直接观察它演化出的稳定块、振荡器和滑翔机。";
        }

        /// <summary>换一个随机种子并立即填充。</summary>
        public void RandomizeSeed()
        {
            _seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            ResetToRandom();
        }

        public void NextPattern() => SelectPattern((_patternIndex + 1) % PatternLibrary.Count);

        public void SetGenerationsPerSecond(float generationsPerSecond)
        {
            _generationsPerSecond = Mathf.Max(0.1f, generationsPerSecond);
            _accumulator = 0f;
        }

        /// <summary>
        /// 用累加器驱动演进，而不是每帧固定一代：代数/秒与帧率解耦，
        /// 且每帧步数有上限，高速档不会拖死主线程。
        /// </summary>
        private void Advance(float deltaTime)
        {
            float interval = 1f / _generationsPerSecond;
            _accumulator += deltaTime;

            int budget = _maxStepsPerFrame;
            while (_accumulator >= interval && budget > 0)
            {
                _accumulator -= interval;
                Kernel.Step();
                budget--;
            }

            // 长时间卡顿后不要追补全部欠账，否则会雪崩。
            if (_accumulator > interval * _maxStepsPerFrame)
                _accumulator = 0f;
        }

        /// <summary>CPU 回退路径下把内核状态同步到一张 Texture2D，供显示层使用。</summary>
        private void RefreshCpuTextureIfNeeded(bool force = false)
        {
            if (Kernel == null || Kernel is GpuGridKernel) return;

            if (_cpuTexture == null || _cpuTexture.width != _width || _cpuTexture.height != _height)
            {
                DestroyCpuTexture();
                _cpuTexture = new Texture2D(_width, _height, TextureFormat.RGBA32, false)
                {
                    name = "GoL_CpuState",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _cpuPixels = new Color32[_width * _height];
                _cpuTextureGeneration = -1;
            }

            if (!force && _cpuTextureGeneration == Kernel.Generation) return;
            _cpuTextureGeneration = Kernel.Generation;

            GridSnapshot snapshot = Kernel.Snapshot();
            Color32 alive = new Color32(255, 255, 255, 255);   // 必须写 255，写 1 会被当成 1/255
            Color32 dead = new Color32(0, 0, 0, 255);

            for (int y = 0; y < _height; y++)
            {
                int row = y * _width;
                for (int x = 0; x < _width; x++)
                    _cpuPixels[row + x] = snapshot.Get(x, y) ? alive : dead;
            }

            _cpuTexture.SetPixels32(_cpuPixels);
            _cpuTexture.Apply(updateMipmaps: false);
        }

        private void DisposeKernel()
        {
            if (Kernel is IDisposable disposable)
                disposable.Dispose();

            Kernel = null;
            IsPlaying = false;
        }

        private void DestroyCpuTexture()
        {
            if (_cpuTexture == null) return;

            if (Application.isPlaying) Destroy(_cpuTexture);
            else DestroyImmediate(_cpuTexture);

            _cpuTexture = null;
            _cpuPixels = null;
            _cpuTextureGeneration = -1;
        }
    }
}
