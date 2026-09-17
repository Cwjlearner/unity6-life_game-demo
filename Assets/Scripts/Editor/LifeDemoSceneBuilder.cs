using System.Collections.Generic;
using GameOfLife.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace GameOfLife.EditorTools
{
    /// <summary>
    /// 用代码生成演示场景：相机 + Canvas + RawImage + 控制组件全部由脚本装配，
    /// 不依赖手工拖拽，因此场景可以随时重新生成、也可以在命令行里一键重建。
    /// </summary>
    public static class LifeDemoSceneBuilder
    {
        private const string ScenePath = "Assets/Scenes/LifeDemo.unity";
        private const string ShaderPath = "Assets/Shaders/LifePingPong.compute";

        [MenuItem("GameOfLife/生成演示场景")]
        public static void BuildDemoScene()
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            if (shader == null)
            {
                Debug.LogError($"[GameOfLife] 找不到 ComputeShader：{ShaderPath}，演示场景未生成。");
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateCamera();
            LifeController controller = CreateLifeController(shader);
            RawImage surface = CreateCanvasWithView(controller.Width, controller.Height);
            AttachDisplay(controller, surface);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            RegisterInBuildSettings();
            SetAsPlayModeStartScene();

            Debug.Log($"[GameOfLife] 演示场景已生成：{ScenePath}（控制器：{controller.name}）");
        }

        /// <summary>
        /// 把演示场景设为 Play 模式的起始场景：无论当前打开的是哪个场景，
        /// 按 Play 都会先加载演示场景，避免"在模板自带的空场景里按了 Play，什么都没发生"。
        /// </summary>
        private static void SetAsPlayModeStartScene()
        {
            var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            if (sceneAsset == null) return;

            if (EditorSceneManager.playModeStartScene == sceneAsset) return;

            EditorSceneManager.playModeStartScene = sceneAsset;
            Debug.Log($"[GameOfLife] 已把 {ScenePath} 设为 Play 起始场景。");
        }

        [MenuItem("GameOfLife/打开演示场景")]
        public static void OpenDemoScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                Debug.Log("[GameOfLife] 演示场景不存在，先生成一次。");
                BuildDemoScene();
                return;
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        private static void CreateCamera()
        {
            var cameraGo = new GameObject("Main Camera");
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 5f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.06f, 0.09f, 1f);
            cameraGo.transform.position = new Vector3(0f, 0f, -10f);
            cameraGo.tag = "MainCamera";
        }

        private static RawImage CreateCanvasWithView(int gridWidth, int gridHeight)
        {
            var canvasGo = new GameObject(
                "Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // 视口容器占屏幕 90%，让画面尽量铺满；具体尺寸由子物体的 AspectRatioFitter 决定。
            var viewportGo = new GameObject("LifeViewport", typeof(RectTransform));
            viewportGo.transform.SetParent(canvasGo.transform, false);

            var viewport = (RectTransform)viewportGo.transform;
            viewport.anchorMin = new Vector2(0.05f, 0.05f);
            viewport.anchorMax = new Vector2(0.95f, 0.95f);
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;

            var viewGo = new GameObject("LifeView", typeof(RawImage));
            viewGo.transform.SetParent(viewport, false);

            var raw = viewGo.GetComponent<RawImage>();
            raw.color = Color.white;

            // 在容器内取"最大且不变形"的尺寸：固定 900×900 在不同分辨率下要么太小要么被裁掉，
            // 而不做比例约束又会把正方形网格拉成屏幕比例。
            var fitter = viewGo.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = gridWidth / (float)Mathf.Max(1, gridHeight);

            return raw;
        }

        private static LifeController CreateLifeController(ComputeShader shader)
        {
            var lifeGo = new GameObject("GameOfLife");
            var controller = lifeGo.AddComponent<LifeController>();

            var controllerSerialized = new SerializedObject(controller);
            controllerSerialized.FindProperty("_computeShader").objectReferenceValue = shader;
            controllerSerialized.ApplyModifiedPropertiesWithoutUndo();

            return controller;
        }

        private static void AttachDisplay(LifeController controller, RawImage surface)
        {
            var display = controller.gameObject.AddComponent<LifeDisplay>();
            display.Controller = controller;
            display.Surface = surface;
            EditorUtility.SetDirty(display);
        }

        private static void RegisterInBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (EditorBuildSettingsScene entry in scenes)
            {
                if (entry.path == ScenePath) return;
            }

            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
