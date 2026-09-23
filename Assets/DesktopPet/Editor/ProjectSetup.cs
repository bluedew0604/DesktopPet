using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DesktopPet.EditorTools
{
    /// <summary>
    /// 투명 데스크톱 펫에 필요한 설정을 한 번에 적용하고 씬을 만든다. 여러 번 실행해도 결과가 같다.
    /// 메뉴: DesktopPet > 1. 프로젝트 자동 설정
    /// </summary>
    public static class ProjectSetup
    {
        public const string ScenePath = "Assets/DesktopPet/Scenes/Main.unity";
        public const string ArtFolder = "Assets/StreamingAssets/Art";

        [MenuItem("DesktopPet/1. 프로젝트 자동 설정", priority = 1)]
        public static void RunMenu()
        {
            string result = Run();
            Debug.Log("[DesktopPet] 자동 설정 완료\n" + result);
            EditorUtility.DisplayDialog("DesktopPet", "자동 설정 완료\n\n" + result, "확인");
        }

        public static string Run()
        {
            var log = new StringBuilder();

            // ---- 플레이어 설정 ----
            PlayerSettings.companyName = "DesktopPet";
            PlayerSettings.productName = "DesktopPet";
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultIsNativeResolution = false;
            PlayerSettings.defaultScreenWidth = 400;
            PlayerSettings.defaultScreenHeight = 300;
            PlayerSettings.resizableWindow = false;
            PlayerSettings.allowFullscreenSwitch = false;
            PlayerSettings.runInBackground = true;          // 포커스가 없어도 계속 움직임
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.forceSingleInstance = true;      // 두 번 실행해도 하나만
            PlayerSettings.useFlipModelSwapchain = false;   // 켜져 있으면 투명 배경이 깨진다
            PlayerSettings.usePlayerLog = true;
            PlayerSettings.SplashScreen.show = false;
            log.AppendLine("플레이어 설정: 창 모드 400x300, 백그라운드 실행, 단일 실행, 플립모델 스왑체인 끔, 스플래시 끔");

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
            log.AppendLine("그래픽 API: Direct3D11만 사용");

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            log.AppendLine("스크립팅 백엔드: Mono");

            if (GraphicsSettings.defaultRenderPipeline != null || QualitySettings.renderPipeline != null)
                log.AppendLine("경고: 렌더 파이프라인 에셋이 지정되어 있음(URP 등). 투명 창은 Built-in에서만 검증됨.");
            else
                log.AppendLine("렌더 파이프라인: Built-in (정상)");

            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.vSyncCount = 0;
                QualitySettings.antiAliasing = 0;
            }
            QualitySettings.SetQualityLevel(QualitySettings.names.Length - 1, true);

            // ---- 그림 폴더 ----
            Directory.CreateDirectory(ArtFolder);
            string readme = Path.Combine(ArtFolder, "그림_넣는_법.txt");
            File.WriteAllText(readme, ArtGuide.Text, new UTF8Encoding(true));
            log.AppendLine("그림 폴더: " + ArtFolder);

            // ---- 씬 ----
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 150;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.transform.position = new Vector3(200, 150, -10);

            var app = new GameObject("DesktopPet");
            app.AddComponent<PetApp>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            log.AppendLine("씬: " + ScenePath + " (빌드 목록에 등록)");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return log.ToString();
        }
    }

    public static class Builder
    {
        public const string OutputPath = "Build/DesktopPet.exe";

        [MenuItem("DesktopPet/2. 빌드하기 (Build 폴더)", priority = 2)]
        public static void BuildMenu()
        {
            string result = Build();
            Debug.Log("[DesktopPet] " + result);
            if (EditorUtility.DisplayDialog("DesktopPet", result, "빌드 폴더 열기", "닫기") && File.Exists(OutputPath))
                EditorUtility.RevealInFinder(Path.GetFullPath(OutputPath));
        }

        [MenuItem("DesktopPet/그림 폴더 열기", priority = 20)]
        public static void OpenArtFolder()
        {
            Directory.CreateDirectory(ProjectSetup.ArtFolder);
            EditorUtility.RevealInFinder(Path.GetFullPath(Path.Combine(ProjectSetup.ArtFolder, "그림_넣는_법.txt")));
        }

        public static string Build()
        {
            if (!File.Exists(ProjectSetup.ScenePath)) ProjectSetup.Run();
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ProjectSetup.ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            var sb = new StringBuilder();
            sb.AppendLine("빌드 결과: " + s.result + "  (오류 " + s.totalErrors + ", 경고 " + s.totalWarnings + ")");
            sb.AppendLine("시간: " + s.totalTime.TotalSeconds.ToString("0.0") + "초, 크기: " + (s.totalSize / 1024 / 1024) + "MB");
            sb.AppendLine("위치: " + Path.GetFullPath(OutputPath));
            foreach (var step in report.steps)
                foreach (var m in step.messages)
                    if (m.type == LogType.Error || m.type == LogType.Exception)
                        sb.AppendLine("오류: " + m.content);
            return sb.ToString();
        }
    }
}
