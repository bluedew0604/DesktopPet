using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ClaudeBridge
{
    /// <summary>
    /// 개발용 자동화 연결 (에디터 전용, 빌드에는 포함되지 않음).
    /// 프로젝트 폴더의 Automation/request.txt 에 "번호 명령" 한 줄이 들어오면 정해진 명령만 실행하고
    /// 결과를 Automation/status.txt 에 적는다. 임의 코드는 실행하지 않는다.
    /// 명령: ping, refresh, setup, build, launch, kill, copylog, playtest 초
    /// 더 이상 필요 없으면 Assets/DesktopPet/Editor/ClaudeBridge 폴더를 지우면 된다.
    /// </summary>
    [InitializeOnLoad]
    internal static class Bridge
    {
        private static readonly string Dir = Path.GetFullPath("Automation");
        private static string RequestPath { get { return Path.Combine(Dir, "request.txt"); } }
        private static string StatusPath { get { return Path.Combine(Dir, "status.txt"); } }
        private static string CompilePath { get { return Path.Combine(Dir, "compile.txt"); } }
        private static string HeartbeatPath { get { return Path.Combine(Dir, "heartbeat.txt"); } }
        private static string LastIdPath { get { return Path.Combine(Dir, "last_id.txt"); } }
        private static string PlaytestLogPath { get { return Path.Combine(Dir, "playtest.txt"); } }

        private const string PendingKey = "ClaudeBridge.PendingRefresh";
        private const string PendingSinceKey = "ClaudeBridge.PendingSince";
        private const string PlaytestUntilKey = "ClaudeBridge.PlaytestUntil";
        private const string PlaytestIdKey = "ClaudeBridge.PlaytestId";

        private static double _nextPoll, _nextHeartbeat;

        static Bridge()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                EditorApplication.update += Poll;
                CompilationPipeline.compilationStarted += OnCompileStarted;
                CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
                if (SessionState.GetString(PlaytestIdKey, "") != "")
                    Application.logMessageReceived += OnPlaytestLog;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ClaudeBridge] init failed: " + ex.Message);
            }
        }

        private static string Stamp() { return DateTime.Now.ToString("HH:mm:ss"); }

        private static void OnCompileStarted(object ctx)
        {
            SafeWrite(CompilePath, "compiling started " + Stamp() + "\n", false);
        }

        private static void OnAssemblyCompiled(string assembly, CompilerMessage[] messages)
        {
            var sb = new StringBuilder();
            int errors = 0;
            foreach (var m in messages)
            {
                if (m.type != CompilerMessageType.Error) continue;
                errors++;
                sb.AppendLine("ERROR " + m.file + ":" + m.line + " " + m.message);
            }
            sb.Insert(0, "[" + Path.GetFileName(assembly) + "] errors=" + errors + "\n");
            SafeWrite(CompilePath, sb.ToString(), true);
        }

        private static void Poll()
        {
            double t = EditorApplication.timeSinceStartup;
            if (t < _nextPoll) return;
            _nextPoll = t + 0.5;

            if (t >= _nextHeartbeat)
            {
                _nextHeartbeat = t + 3;
                SafeWrite(HeartbeatPath, Stamp() + " compiling=" + EditorApplication.isCompiling + " playing=" + EditorApplication.isPlaying
                    + " compileFailed=" + EditorUtility.scriptCompilationFailed + " updating=" + EditorApplication.isUpdating, false);
            }

            CheckPendingRefresh(t);
            CheckPlaytest();

            if (!File.Exists(RequestPath)) return;
            string req;
            try { req = File.ReadAllText(RequestPath).Trim(); } catch (Exception) { return; }
            if (req.Length == 0) return;
            string[] parts = req.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            string id = parts[0];
            string lastId = File.Exists(LastIdPath) ? SafeRead(LastIdPath).Trim() : "";
            if (id == lastId) return;
            SafeWrite(LastIdPath, id, false);
            string cmd = parts.Length > 1 ? parts[1].ToLowerInvariant() : "ping";
            string arg = parts.Length > 2 ? parts[2] : "";

            try { Run(id, cmd, arg); }
            catch (Exception ex) { Status(id, cmd, "error", ex.ToString()); }
        }

        private static void Run(string id, string cmd, string arg)
        {
            switch (cmd)
            {
                case "ping":
                    Status(id, cmd, "done", "pong. unity " + Application.unityVersion);
                    break;

                case "refresh":
                    Status(id, cmd, "running", "");
                    SessionState.SetString(PendingKey, id);
                    SessionState.SetFloat(PendingSinceKey, (float)EditorApplication.timeSinceStartup);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    break;

                case "setup":
                    Status(id, cmd, "running", "");
                    Status(id, cmd, "done", Invoke("DesktopPet.EditorTools.ProjectSetup, DesktopPet.Editor", "Run"));
                    break;

                case "build":
                    Status(id, cmd, "running", "");
                    Status(id, cmd, "done", Invoke("DesktopPet.EditorTools.Builder, DesktopPet.Editor", "Build"));
                    break;

                case "launch":
                {
                    string exe = Path.GetFullPath("Build/DesktopPet.exe");
                    if (!File.Exists(exe)) { Status(id, cmd, "error", "no build at " + exe); break; }
                    var p = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
                    Status(id, cmd, "done", "started pid " + (p != null ? p.Id.ToString() : "?"));
                    break;
                }

                case "kill":
                {
                    int n = 0;
                    foreach (var p in Process.GetProcessesByName("DesktopPet")) { try { p.Kill(); n++; } catch (Exception) { } }
                    Status(id, cmd, "done", "killed " + n);
                    break;
                }

                case "copylog":
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string lowDir = Path.Combine(Path.GetDirectoryName(local), "LocalLow", PlayerSettings.companyName, PlayerSettings.productName);
                    var sb = new StringBuilder();
                    foreach (var name in new[] { "Player.log", "Player-prev.log", "settings.json", "diary.json", "일기.txt" })
                    {
                        string src = Path.Combine(lowDir, name);
                        if (File.Exists(src)) { File.Copy(src, Path.Combine(Dir, "app_" + name), true); sb.AppendLine("copied " + name); }
                        else sb.AppendLine("missing " + src);
                    }
                    Status(id, cmd, "done", sb.ToString());
                    break;
                }

                case "playtest":
                {
                    float secs;
                    if (!float.TryParse(arg, out secs)) secs = 8f;
                    SafeWrite(PlaytestLogPath, "playtest " + Stamp() + " for " + secs + "s\n", false);
                    SessionState.SetString(PlaytestIdKey, id);
                    SessionState.SetFloat(PlaytestUntilKey, (float)(EditorApplication.timeSinceStartup + secs + 3));
                    Application.logMessageReceived -= OnPlaytestLog;
                    Application.logMessageReceived += OnPlaytestLog;
                    Status(id, cmd, "running", "");
                    EditorApplication.isPlaying = true;
                    break;
                }

                default:
                    Status(id, cmd, "error", "unknown command");
                    break;
            }
        }

        private static void CheckPendingRefresh(double t)
        {
            string pending = SessionState.GetString(PendingKey, "");
            if (pending == "") return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            float since = SessionState.GetFloat(PendingSinceKey, 0f);
            if (t - since < 2.0 && t >= since) return;
            SessionState.EraseString(PendingKey);
            Status(pending, "refresh", "done", "compileFailed=" + EditorUtility.scriptCompilationFailed + "\n" + SafeRead(CompilePath));
        }

        private static void CheckPlaytest()
        {
            string id = SessionState.GetString(PlaytestIdKey, "");
            if (id == "") return;
            float until = SessionState.GetFloat(PlaytestUntilKey, 0f);
            if (EditorApplication.timeSinceStartup < until) return;
            if (EditorApplication.isPlaying) { EditorApplication.isPlaying = false; return; }
            SessionState.EraseString(PlaytestIdKey);
            Application.logMessageReceived -= OnPlaytestLog;
            Status(id, "playtest", "done", SafeRead(PlaytestLogPath));
        }

        private static void OnPlaytestLog(string condition, string stack, LogType type)
        {
            string line = "[" + type + "] " + condition;
            if (type == LogType.Exception || type == LogType.Error) line += "\n" + stack;
            SafeWrite(PlaytestLogPath, line + "\n", true);
        }

        private static string Invoke(string typeName, string method)
        {
            var type = Type.GetType(typeName);
            if (type == null) return "type not found: " + typeName + " (compile error?) compileFailed=" + EditorUtility.scriptCompilationFailed;
            var m = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            return m == null ? "method not found: " + method : (string)m.Invoke(null, null);
        }

        private static void Status(string id, string cmd, string state, string message)
        {
            SafeWrite(StatusPath, id + " " + cmd + " " + state + " " + Stamp() + "\n" + message, false);
        }

        private static void SafeWrite(string path, string text, bool append)
        {
            try
            {
                if (append) File.AppendAllText(path, text, Encoding.UTF8);
                else File.WriteAllText(path, text, Encoding.UTF8);
            }
            catch (Exception) { }
        }

        private static string SafeRead(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; } catch (Exception) { return ""; }
        }
    }
}
