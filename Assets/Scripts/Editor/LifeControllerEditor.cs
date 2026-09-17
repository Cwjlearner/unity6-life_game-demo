using GameOfLife.Core;
using GameOfLife.Gpu;
using GameOfLife.Presentation;
using UnityEditor;
using UnityEngine;

namespace GameOfLife.EditorTools
{
    /// <summary>
    /// LifeController 的自定义检视面板：把"选构型 / 启停 / 单步 / 重置 / 调速 / 换档位"
    /// 从"运行后靠代码调"变成"点上就能用"，并实时显示代数、活细胞数与构型分析结论。
    /// </summary>
    [CustomEditor(typeof(LifeController))]
    public sealed class LifeControllerEditor : Editor
    {
        private SerializedProperty _computeShader;
        private SerializedProperty _width;
        private SerializedProperty _height;
        private SerializedProperty _wrapEdges;
        private SerializedProperty _stepMode;
        private SerializedProperty _playOnStart;
        private SerializedProperty _generationsPerSecond;
        private SerializedProperty _maxStepsPerFrame;
        private SerializedProperty _patternIndex;
        private SerializedProperty _enableKeyboardShortcuts;
        private SerializedProperty _seed;
        private SerializedProperty _aliveProbability;
        private SerializedProperty _initialSource;

        private void OnEnable()
        {
            _computeShader = serializedObject.FindProperty("_computeShader");
            _width = serializedObject.FindProperty("_width");
            _height = serializedObject.FindProperty("_height");
            _wrapEdges = serializedObject.FindProperty("_wrapEdges");
            _stepMode = serializedObject.FindProperty("_stepMode");
            _playOnStart = serializedObject.FindProperty("_playOnStart");
            _generationsPerSecond = serializedObject.FindProperty("_generationsPerSecond");
            _maxStepsPerFrame = serializedObject.FindProperty("_maxStepsPerFrame");
            _patternIndex = serializedObject.FindProperty("_patternIndex");
            _enableKeyboardShortcuts = serializedObject.FindProperty("_enableKeyboardShortcuts");
            _seed = serializedObject.FindProperty("_seed");
            _aliveProbability = serializedObject.FindProperty("_aliveProbability");
            _initialSource = serializedObject.FindProperty("_initialSource");
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            var controller = (LifeController)target;

            serializedObject.Update();
            bool configChanged = DrawConfiguration();
            serializedObject.ApplyModifiedProperties();

            // 配置变化后重建内核：尺寸、边界、档位、构型都要重新生效。
            if (configChanged && controller.Kernel != null)
                controller.Rebuild();

            EditorGUILayout.Space();
            DrawTransport(controller);

            EditorGUILayout.Space();
            DrawStatus(controller);
        }

        private bool DrawConfiguration()
        {
            EditorGUILayout.LabelField("模拟", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.PropertyField(_computeShader, new GUIContent("Compute Shader"));
            EditorGUILayout.PropertyField(_width, new GUIContent("网格宽"));
            EditorGUILayout.PropertyField(_height, new GUIContent("网格高"));
            EditorGUILayout.PropertyField(_wrapEdges, new GUIContent("环面边界"));

            DrawStepModePopup();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("播放", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_playOnStart, new GUIContent("进入 Play 即开始"));
            EditorGUILayout.PropertyField(_generationsPerSecond, new GUIContent("代数 / 秒"));
            EditorGUILayout.PropertyField(_maxStepsPerFrame, new GUIContent("每帧步数上限"));
            EditorGUILayout.PropertyField(_enableKeyboardShortcuts, new GUIContent("启用快捷键"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("构型", EditorStyles.boldLabel);
            DrawInitialSourcePopup();

            using (new EditorGUI.DisabledScope(
                       _initialSource.enumValueIndex != (int)InitialStateSource.PresetPattern))
            {
                DrawPatternPopup();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("随机初始状态", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_seed, new GUIContent("种子"));
            EditorGUILayout.PropertyField(_aliveProbability, new GUIContent("存活密度"));
            EditorGUILayout.LabelField(
                " ",
                "同一个种子在 CPU 与 GPU 上得到完全相同的棋盘，可复现、可比对。密度 0.3 左右最有趣。",
                EditorStyles.miniLabel);

            return EditorGUI.EndChangeCheck();
        }

        /// <summary>初始状态来源：预制构型 / 固定种子的随机 / 每次换种子的随机。</summary>
        private void DrawInitialSourcePopup()
        {
            string[] labels =
            {
                "预制构型（从 6 种里选）",
                "随机 · 固定种子（可复现）",
                "随机 · 每次换种子（真随机）",
            };

            int current = Mathf.Clamp(_initialSource.enumValueIndex, 0, labels.Length - 1);
            int selected = EditorGUILayout.Popup(new GUIContent("初始状态来源"), current, labels);
            if (selected != current)
                _initialSource.enumValueIndex = selected;

            string hint = (InitialStateSource)_initialSource.enumValueIndex switch
            {
                InitialStateSource.RandomWithSeed =>
                    "重置和进入 Play 时都用下面这颗种子生成随机棋盘——同一颗种子永远得到同一张棋盘。",
                InitialStateSource.RandomEachTime =>
                    "重置和进入 Play 时都会换一颗新种子，每次局面都不一样。适合随便看看演化。",
                _ => "重置和进入 Play 时，铺上当前选中的那种预制构型。",
            };

            EditorGUILayout.LabelField(" ", hint, EditorStyles.miniLabel);
        }

        private void DrawPatternPopup()
        {
            var names = new string[PatternLibrary.Count];
            for (int i = 0; i < names.Length; i++)
            {
                PatternDefinition entry = PatternLibrary.Get(i);
                names[i] = $"{entry.DisplayName}（{entry.Name}）";
            }

            int current = Mathf.Clamp(_patternIndex.intValue, 0, names.Length - 1);
            int selected = EditorGUILayout.Popup(new GUIContent("预制构型"), current, names);
            if (selected != current)
                _patternIndex.intValue = selected;

            PatternDefinition pattern = PatternLibrary.Get(_patternIndex.intValue);
            EditorGUILayout.LabelField(
                " ",
                $"{DescribeCategory(pattern.Category)} · {pattern.Width}×{pattern.Height} · {pattern.Cells.Count} 细胞 · " +
                $"周期 {pattern.Period} · 位移 ({pattern.Displacement.X},{pattern.Displacement.Y})",
                EditorStyles.miniLabel);
        }

        private static string DescribeCategory(PatternCategory category) => category switch
        {
            PatternCategory.StillLife => "稳定静物（一直不变）",
            PatternCategory.Oscillator => "振荡器（来回往复）",
            PatternCategory.Spaceship => "循环震荡 / 飞船（边平移边重复）",
            _ => category.ToString(),
        };

        private static void DrawStepModeHint(int index)
        {
            string hint = (GpuStepMode)index switch
            {
                GpuStepMode.Basic => "每像素 1 个细胞，每个线程直接采样 8 个邻居。最直观，速度最慢。",
                GpuStepMode.SharedMemory => "线程组先把含边框的整块载入共享内存，邻居从共享内存读。没有尺寸限制。",
                GpuStepMode.PackedChannels => "1 个像素存 4 个细胞（RGBA 四通道）。要求网格宽是 4 的倍数。",
                GpuStepMode.BitPacked => "1 个整数存 32 个细胞，用位运算统计邻居。要求网格宽是 32 的倍数，最省显存。",
                _ => string.Empty,
            };

            EditorGUILayout.LabelField(" ", hint, EditorStyles.miniLabel);
        }

        /// <summary>档位下拉用中文标签：枚举名保留英文（代码与日志里用），界面显示中文。</summary>
        private void DrawStepModePopup()
        {
            string[] labels =
            {
                "基础版（最好懂，最慢）",
                "档一 共享内存（无尺寸限制）",
                "档二 四通道打包（推荐）",
                "档三 位打包（最快 / 最省显存）",
            };

            int current = Mathf.Clamp(_stepMode.enumValueIndex, 0, labels.Length - 1);
            int selected = EditorGUILayout.Popup(new GUIContent("档位"), current, labels);
            if (selected != current)
                _stepMode.enumValueIndex = selected;

            DrawStepModeHint(_stepMode.enumValueIndex);
        }

        private static void DrawTransport(LifeController controller)
        {
            EditorGUILayout.LabelField("控制", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(controller.IsPlaying ? "暂停" : "播放"))
                    controller.TogglePlay();

                if (GUILayout.Button("单步"))
                    controller.StepOnce();

                if (GUILayout.Button("重置"))
                {
                    controller.ResetToInitial();
                    EditorUtility.SetDirty(controller);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("下一个构型"))
                {
                    controller.NextPattern();
                    EditorUtility.SetDirty(controller);
                }

                if (GUILayout.Button("重新分析"))
                    controller.AnalyzeCurrentPattern();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("按种子随机填充"))
                {
                    controller.ResetToRandom();
                    EditorUtility.SetDirty(controller);
                }

                if (GUILayout.Button("换种子并填充"))
                {
                    controller.RandomizeSeed();
                    EditorUtility.SetDirty(controller);
                }
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "编辑器未运行时内核不会创建（避免占用显存与泄漏 RenderTexture）。" +
                    "进入 Play 模式后这里会显示实时状态，控制按钮也才生效。",
                    MessageType.Info);
            }
        }

        private static void DrawStatus(LifeController controller)
        {
            EditorGUILayout.LabelField("运行状态", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("实际内核", DescribeKernel(controller));
                EditorGUILayout.IntField("代数", controller.Generation);
                EditorGUILayout.IntField("活细胞", controller.Population);
                EditorGUILayout.TextField("检测周期", controller.DetectedPeriod > 0
                    ? controller.DetectedPeriod.ToString()
                    : "—");
            }

            if (!string.IsNullOrEmpty(controller.AnalysisMessage))
                EditorGUILayout.HelpBox(controller.AnalysisMessage, MessageType.None);

            if (!string.IsNullOrEmpty(controller.FallbackMessage))
                EditorGUILayout.HelpBox(controller.FallbackMessage, MessageType.Warning);
        }

        private static string DescribeKernel(LifeController controller)
        {
            if (controller.Kernel == null) return "（未创建）";

            return controller.Kernel is GpuGridKernel gpu
                ? $"GPU · {gpu.StepMode}"
                : "CPU 参考实现（回退）";
        }
    }
}
