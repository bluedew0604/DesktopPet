using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DesktopPet.Core;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DesktopPet
{
    /// <summary>
    /// 게임 전체를 조립하는 유일한 컴포넌트. 씬에는 카메라와 이 컴포넌트 하나만 있으면 된다.
    /// 매 프레임 순서: 창 유지 → 입력(클릭/드래그/트레이) → 엔진 Tick → 화면 반영.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PetApp : MonoBehaviour
    {
        [Header("창 크기 (논리 px, 화면 배율은 자동 반영)")]
        public int windowWidth = 400;
        public int windowHeight = 300;

        [Header("캐릭터 그림 캔버스 전체가 화면에서 차지할 높이 (논리 px)")]
        public float characterHeight = 150f;

        // 디버그용: true로 바꾸면 마우스 입력 처리 과정을 Player.log에 남긴다 (씬에 저장되지 않음)
        [System.NonSerialized] public bool logInput = false;
        private bool _lastHoverLogged, _lastThroughLogged = true;

        private const int MenuNormal = 1, MenuQuiet = 2, MenuHide = 3, MenuDiary = 4, MenuDiaryFolder = 5,
            MenuResetPos = 6, MenuIdleHint = 7, MenuReloadArt = 8, MenuQuit = 9;

        private PetBrain _brain;
        private ArtLibrary _art;
        private PetView _view;
        private BubbleUI _ui;
        private DesktopWindow _win;
        private TrayIcon _tray;
        private SaveService _save;
        private PetSettings _settings;
        private HostClock _clock;
        private Camera _cam;

        private bool _firstRun;
        private float _greetAt = -1f;
        private bool _pressed, _dragging, _hovering, _menuPending;
        private Vector2Int _pressCursor, _pressWindowPos;
        private Vector2 _pressPointer, _editorOffset, _editorOffsetAtPress;
        private float _pressVisual, _lastButtonSeenDown;
        private int _lastW, _lastH;
        private float _lastScale;
        private bool _quitting;

        private void Awake()
        {
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            QualitySettings.antiAliasing = 0;
            Application.targetFrameRate = 30;

            SetupCamera();

            _clock = new HostClock();
            _save = new SaveService(Application.persistentDataPath);
            _settings = _save.LoadSettings(out _firstRun);
            var diary = _save.LoadDiary();

            _brain = new PetBrain(Environment.TickCount, null, diary);
            _brain.Restore(_settings.CushionMessy, _settings.Quiet, _settings.IdleHint);

            _art = new ArtLibrary();
            float t0 = Time.realtimeSinceStartup;
            _art.Load();
            Debug.Log("[DesktopPet] 그림 불러오기 " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("0") + "ms\n" + _art.Report);

            _view = new PetView(transform, _art) { CharacterHeight = characterHeight };
            _ui = new BubbleUI(transform);
            _win = new DesktopWindow();
            _tray = new TrayIcon();
        }

        private void Start()
        {
            _win.Init(windowWidth, windowHeight, _settings.HasPosition, _settings.PosFromRight, _settings.PosFromBottom);
            if (_win.Active)
            {
                if (!WindowHooks.Install(_win.Hwnd)) Debug.LogWarning("[DesktopPet] 창 메시지 연결 실패 — 클릭 반응이 느릴 수 있음");
                _tray.Add(_win.Hwnd, WriteTrayIcon(), "DesktopPet");
            }
            else if (!Application.isEditor)
            {
                Debug.LogWarning("[DesktopPet] 데스크톱 창 모드 실패: " + _win.LastError);
            }

            Debug.Log("[DesktopPet] 시작: 데스크톱모드=" + _win.Active + " 배율=" + _win.Scale + " 저장폴더=" + _save.Folder);
            if (_firstRun) _greetAt = Time.unscaledTime + 1.2f;
        }

        private void Update()
        {
            double now = _clock.Now();
            DateTime local = DateTime.Now;

            _win.Maintain(Time.unscaledDeltaTime);
            if (_art.Loading) _art.ContinueLoading();
            UpdateLayout();

            if (_win.Active) HandleDesktopInput(now, local);
            else HandleEditorInput(now, local);

            double? idle = _brain.IdleHintEnabled ? _win.UserIdleSeconds() : null;
            _brain.Tick(now, local, idle);

            PetEvent e;
            while (_brain.TryDequeueEvent(out e)) OnBrainEvent(e);

            if (_greetAt > 0 && Time.unscaledTime >= _greetAt)
            {
                _greetAt = -1f;
                _ui.ShowBubble("안녕! 여기서 지낼게.", 4f);
            }

            var frame = _brain.Evaluate(now);
            _pressVisual = Mathf.MoveTowards(_pressVisual, _pressed && !_dragging ? 1f : 0f, Time.unscaledDeltaTime * 12f);
            _view.Apply(frame, _pressVisual, Time.unscaledTime);
            _ui.Tick(_view.HeadWorld(), frame.Clip == ClipNames.Sleep, _brain.Hidden, Screen.width, Screen.height);

            if (_brain.CushionMessy != _settings.CushionMessy)
            {
                _settings.CushionMessy = _brain.CushionMessy;
                _save.SaveSettings(_settings);
            }

            if (_menuPending)
            {
                _menuPending = false;
                ShowTrayMenu();
            }

            ApplyFrameRate(frame);
        }

        // =================================================================
        // 입력: 실제 바탕화면 (빌드)
        // =================================================================

        private void HandleDesktopInput(double now, DateTime local)
        {
            Vector2 client;
            bool inside;
            _win.TryGetCursorClient(out client, out inside);
            _hovering = inside && !_brain.Hidden && _view.HitTest(client, 6f);

            HookEvent ev;
            while (WindowHooks.TryDequeue(out ev))
            {
                if (logInput) Debug.Log("[입력] 창 메시지: " + ev + " 커서(창안)=" + client + " 판정=" + _view.HitTest(client, 12f));
                switch (ev)
                {
                    case HookEvent.MouseDown:
                        if (!_pressed && !_brain.Hidden && _view.HitTest(client, 12f)) BeginPress(client);
                        break;
                    case HookEvent.MouseUp:
                        EndPress(now, local, false);
                        break;
                    case HookEvent.CaptureLost:
                        EndPress(now, local, true);
                        break;
                    case HookEvent.TrayLeftClick:
                        if (_brain.Hidden) SetHidden(false, now, local);
                        else ToggleDiary();
                        break;
                    case HookEvent.TrayRightClick:
                        _menuPending = true;
                        break;
                    case HookEvent.TaskbarRestarted:
                        _tray.Readd();
                        break;
                }
            }

            if (_pressed)
            {
                // 뗌 메시지를 혹시 놓쳤을 때의 안전장치 (왼손잡이 버튼 설정도 고려)
                int vk = Win32.GetSystemMetrics(23) != 0 ? 0x02 : Win32.VK_LBUTTON;
                if (Win32.IsKeyDown(vk)) _lastButtonSeenDown = Time.unscaledTime;
                else if (Time.unscaledTime - _lastButtonSeenDown > 0.3f) EndPress(now, local, false);
            }

            if (_pressed)
            {
                Vector2Int cursor;
                if (_win.TryGetCursorScreen(out cursor))
                {
                    int dx = cursor.x - _pressCursor.x, dy = cursor.y - _pressCursor.y;
                    if (!_dragging && DragMath.IsDrag(dx, dy, _win.Scale))
                    {
                        _dragging = true;
                        _brain.DragStart(now, local);
                        _ui.HideDiary();
                        if (logInput) Debug.Log("[입력] 드래그 시작");
                    }
                    if (_dragging) _win.MoveTo(_pressWindowPos.x + dx, _pressWindowPos.y + dy);
                }
            }

            bool through = _brain.Hidden || !(_hovering || _pressed);
            _win.SetClickThrough(through);
            if (logInput && (_hovering != _lastHoverLogged || through != _lastThroughLogged))
            {
                Debug.Log("[입력] hover=" + _hovering + " 클릭통과=" + through + " 커서(창안)=" + client + " inside=" + inside
                    + " 화면=" + Screen.width + "x" + Screen.height + " 캐릭터머리=" + _view.HeadWorld());
                _lastHoverLogged = _hovering;
                _lastThroughLogged = through;
            }
        }

        private void BeginPress(Vector2 pointer)
        {
            _pressed = true;
            _dragging = false;
            _pressPointer = pointer;
            _lastButtonSeenDown = Time.unscaledTime;
            _win.TryGetCursorScreen(out _pressCursor);
            _win.TryGetWindowPos(out _pressWindowPos);
            _editorOffsetAtPress = _editorOffset;
        }

        private void EndPress(double now, DateTime local, bool cancelled)
        {
            if (!_pressed) return;
            if (_dragging)
            {
                _brain.DragEnd(now, local);
                SavePosition();
                if (logInput) Debug.Log("[입력] 드래그 끝");
            }
            else if (!cancelled)
            {
                bool ok = _brain.Click(now, local);
                if (logInput) Debug.Log("[입력] 클릭 → 반응=" + ok + " 현재행동=" + _brain.Current);
            }
            _pressed = false;
            _dragging = false;
            WindowHooks.EndCapture();
        }

        // =================================================================
        // 입력: 에디터 Play 모드 (창 조작 없이 동작 확인용)
        // =================================================================

        private void HandleEditorInput(double now, DateTime local)
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse != null)
            {
                Vector2 p = mouse.position.ReadValue();
                _hovering = _view.HitTest(p, 6f);
                if (mouse.leftButton.wasPressedThisFrame && _view.HitTest(p, 12f)) BeginPress(p);
                if (_pressed && mouse.leftButton.isPressed)
                {
                    Vector2 d = p - _pressPointer;
                    if (!_dragging && DragMath.IsDrag(d.x, d.y, 1.0))
                    {
                        _dragging = true;
                        _brain.DragStart(now, local);
                    }
                    if (_dragging) _editorOffset = _editorOffsetAtPress + d;
                }
                if (_pressed && mouse.leftButton.wasReleasedThisFrame) EndPress(now, local, false);
            }

            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.digit1Key.wasPressedThisFrame) _brain.DebugStart(ActionKind.SnackScene, now, local);
                if (kb.digit2Key.wasPressedThisFrame) _brain.DebugStart(ActionKind.CushionScene, now, local);
                if (kb.digit3Key.wasPressedThisFrame) _brain.DebugStart(ActionKind.PlayScene, now, local);
                if (kb.zKey.wasPressedThisFrame) _brain.DebugStart(ActionKind.Doze, now, local);
                if (kb.sKey.wasPressedThisFrame) _brain.DebugStart(ActionKind.Sleep, now, local);
                if (kb.wKey.wasPressedThisFrame) _brain.DebugStart(ActionKind.Walk, now, local);
                if (kb.qKey.wasPressedThisFrame) SetQuiet(!_brain.Quiet, now, local);
                if (kb.hKey.wasPressedThisFrame) SetHidden(!_brain.Hidden, now, local);
                if (kb.dKey.wasPressedThisFrame) ToggleDiary();
                if (kb.rKey.wasPressedThisFrame) ReloadArt();
            }
#endif
        }

        // =================================================================
        // 트레이 메뉴 / 모드
        // =================================================================

        private void ShowTrayMenu()
        {
            if (!_win.Active) return;
            var items = new List<TrayIcon.MenuItem>
            {
                new TrayIcon.MenuItem { Id = MenuNormal, Text = "보통 모드", Checked = !_brain.Quiet },
                new TrayIcon.MenuItem { Id = MenuQuiet, Text = "조용히 모드", Checked = _brain.Quiet },
                new TrayIcon.MenuItem { Id = 0 },
                new TrayIcon.MenuItem { Id = MenuHide, Text = _brain.Hidden ? "다시 보이기" : "숨기기" },
                new TrayIcon.MenuItem { Id = MenuDiary, Text = "오늘 일기 보기", Disabled = _brain.Hidden },
                new TrayIcon.MenuItem { Id = MenuDiaryFolder, Text = "일기 폴더 열기" },
                new TrayIcon.MenuItem { Id = MenuResetPos, Text = "제자리(오른쪽 아래)로" },
                new TrayIcon.MenuItem { Id = 0 },
                new TrayIcon.MenuItem { Id = MenuIdleHint, Text = "입력 없는 시간 참고하기 (선택)", Checked = _brain.IdleHintEnabled },
                new TrayIcon.MenuItem { Id = MenuReloadArt, Text = "그림 다시 불러오기" },
                new TrayIcon.MenuItem { Id = 0 },
                new TrayIcon.MenuItem { Id = MenuQuit, Text = "종료" },
            };

            int cmd = _tray.ShowMenu(items);
            double now = _clock.Now();
            DateTime local = DateTime.Now;
            switch (cmd)
            {
                case MenuNormal: SetQuiet(false, now, local); break;
                case MenuQuiet: SetQuiet(true, now, local); break;
                case MenuHide: SetHidden(!_brain.Hidden, now, local); break;
                case MenuDiary: ToggleDiary(); break;
                case MenuDiaryFolder: OpenFolder(_save.Folder); break;
                case MenuResetPos: _win.ResetPosition(); SavePosition(); break;
                case MenuIdleHint:
                    _brain.IdleHintEnabled = !_brain.IdleHintEnabled;
                    _settings.IdleHint = _brain.IdleHintEnabled;
                    _save.SaveSettings(_settings);
                    break;
                case MenuReloadArt: ReloadArt(); break;
                case MenuQuit: Quit(); break;
            }
        }

        private void SetQuiet(bool quiet, double now, DateTime local)
        {
            _brain.SetQuiet(quiet, now, local);
            _settings.Quiet = quiet;
            _save.SaveSettings(_settings);
        }

        private void SetHidden(bool hidden, double now, DateTime local)
        {
            if (hidden && _pressed) { _pressed = false; _dragging = false; WindowHooks.EndCapture(); }
            _brain.SetHidden(hidden, now, local);
            _view.SetHidden(hidden);
            if (hidden) _ui.HideAll();
        }

        private void ToggleDiary()
        {
            if (_ui.DiaryVisible) { _ui.HideDiary(); return; }
            var today = DateTime.Now;
            var day = _brain.Diary.GetDay(today);
            var sb = new System.Text.StringBuilder();
            sb.Append("오늘의 일기 · ").Append(today.Month).Append("월 ").Append(today.Day).Append("일");
            if (day == null || day.Entries.Count == 0)
            {
                sb.Append("\n\n아직 적을 만한 일이 없었다.");
            }
            else
            {
                sb.Append('\n');
                foreach (var en in day.Entries) sb.Append('\n').Append(en.Time).Append("  ").Append(en.Text);
            }
            _ui.ShowDiary(sb.ToString(), 12f);
        }

        private void ReloadArt()
        {
            _art.Load();
            Debug.Log("[DesktopPet] 그림 다시 불러옴\n" + _art.Report);
            if (_win.Active) _tray.ReplaceIcon(WriteTrayIcon());
        }

        private void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + path.Replace('/', '\\') + "\"");
            }
            catch (Exception ex) { Debug.LogWarning("[DesktopPet] 폴더 열기 실패: " + ex.Message); }
        }

        // =================================================================
        // 엔진 이벤트 / 저장
        // =================================================================

        private void OnBrainEvent(PetEvent e)
        {
            switch (e.Kind)
            {
                case PetEventKind.Bubble:
                    _ui.ShowBubble(e.Text, (float)e.Duration);
                    break;
                case PetEventKind.DiaryChanged:
                    _save.SaveDiary(_brain.Diary);
                    break;
                case PetEventKind.ActionStarted:
                    if (ActionInfo.IsScene(e.Action) || e.Action == ActionKind.Sleep)
                        Debug.Log("[DesktopPet] 시작: " + e.Action);
                    break;
                case PetEventKind.ActionEnded:
                    if (ActionInfo.IsScene(e.Action) || e.Action == ActionKind.Sleep)
                        Debug.Log("[DesktopPet] 끝: " + e.Action + " (" + (e.Reason == EndReason.Completed ? "완료" : "중단") + ")");
                    break;
            }
        }

        private void SavePosition()
        {
            double r, b;
            if (_win.TryGetSavePosition(out r, out b))
            {
                _settings.HasPosition = true;
                _settings.PosFromRight = r;
                _settings.PosFromBottom = b;
                _save.SaveSettings(_settings);
            }
        }

        private string WriteTrayIcon()
        {
            try
            {
                string path = Path.Combine(Application.temporaryCachePath, "tray.ico");
                File.WriteAllBytes(path, IcoWriter.Build(_art.TrayTexture, _art.TrayRect, new[] { 16, 20, 24, 32, 40, 48, 64 }));
                return path;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DesktopPet] 트레이 아이콘 만들기 실패: " + ex.Message);
                return null;
            }
        }

        // =================================================================
        // 화면
        // =================================================================

        private void SetupCamera()
        {
            _cam = Camera.main;
            if (_cam == null)
            {
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                _cam = go.AddComponent<Camera>();
            }
            _cam.orthographic = true;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f); // 알파 0 = 투명
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 100f;
            _cam.transform.rotation = Quaternion.identity;
        }

        private void UpdateLayout()
        {
            float scale = _win.Active ? _win.Scale : 1f;
            if (Screen.width != _lastW || Screen.height != _lastH || !Mathf.Approximately(scale, _lastScale))
            {
                _lastW = Screen.width;
                _lastH = Screen.height;
                _lastScale = scale;
                Debug.Log("[DesktopPet] 화면 " + Screen.width + "x" + Screen.height + " 배율 " + scale);
                _cam.orthographicSize = Screen.height * 0.5f;
                _cam.transform.position = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, -10f);
                _ui.SetScale(scale);
            }
            _view.Layout(Screen.width, scale, _editorOffset);
        }

        private void ApplyFrameRate(Frame f)
        {
            int fps;
            if (_brain.Hidden) fps = 5;
            else if (_pressed || _dragging || _hovering) fps = 60;
            else if (f.Clip == ClipNames.Sleep) fps = 20;
            else fps = 30;
            if (Application.targetFrameRate != fps) Application.targetFrameRate = fps;
        }

        private void OnApplicationQuit()
        {
            Shutdown();
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private void Shutdown()
        {
            if (_quitting) return;
            _quitting = true;
            try
            {
                SavePosition();
                if (_settings != null) _save.SaveSettings(_settings);
                if (_brain != null) _save.SaveDiary(_brain.Diary);
            }
            catch (Exception ex) { Debug.LogWarning("[DesktopPet] 종료 저장 실패: " + ex.Message); }
            if (_tray != null) _tray.Remove();
            WindowHooks.Uninstall();
            if (_art != null) _art.Unload();
        }
    }
}
