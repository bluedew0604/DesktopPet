using System;
using System.Text;
using UnityEngine;

namespace DesktopPet
{
    /// <summary>
    /// 투명 · 항상 위 · 작업표시줄에 안 보임 · 클릭해도 포커스를 뺏지 않는 작은 창.
    /// 캐릭터 위에 마우스가 있을 때만 클릭을 받고, 나머지 영역은 클릭이 아래 창으로 통과한다.
    /// 에디터에서는 아무것도 하지 않는다(Active=false).
    /// </summary>
    public sealed class DesktopWindow
    {
        public bool Active { get; private set; }
        public IntPtr Hwnd { get; private set; }
        public float Scale { get; private set; }   // 화면 배율 (100% = 1, 150% = 1.5)
        public int LogicalWidth { get; private set; }
        public int LogicalHeight { get; private set; }
        public string LastError { get; private set; }

        private bool _clickThrough = true;
        private Win32.RECT _work;
        private uint _dpi;
        private float _topmostTimer;

        private static IntPtr _found;
        private static uint _myPid;
        private static readonly Win32.EnumWindowsProc EnumCallback = OnEnumWindow;

        public DesktopWindow()
        {
            Scale = 1f;
        }

        public void Init(int logicalWidth, int logicalHeight, bool hasSavedPos, double fromRight, double fromBottom)
        {
            LogicalWidth = logicalWidth;
            LogicalHeight = logicalHeight;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            try
            {
                Hwnd = FindMainWindow();
                if (Hwnd == IntPtr.Zero) { LastError = "Unity 창을 찾지 못함"; return; }

                _dpi = ReadDpi();
                Scale = _dpi / 96f;
                _work = ReadWorkArea();

                // 테두리 없는 팝업 창 + 투명/클릭통과/툴윈도우(작업표시줄 X)/포커스 안 뺏기
                Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
                Win32.SetStyle(Hwnd, Win32.GWL_STYLE, Win32.WS_POPUP | Win32.WS_VISIBLE);
                uint ex = Win32.GetStyle(Hwnd, Win32.GWL_EXSTYLE);
                ex &= ~Win32.WS_EX_APPWINDOW;
                ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
                Win32.SetStyle(Hwnd, Win32.GWL_EXSTYLE, ex);
                _clickThrough = true;

                var margins = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Win32.DwmExtendFrameIntoClientArea(Hwnd, ref margins);

                int w, h;
                PhysicalSize(out w, out h);
                int x, y;
                if (hasSavedPos)
                {
                    x = (int)Math.Round(_work.Right - fromRight * Scale - w);
                    y = (int)Math.Round(_work.Bottom - fromBottom * Scale - h);
                }
                else
                {
                    DefaultPosition(w, h, out x, out y);
                }
                Clamp(ref x, ref y, w, h);
                Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, x, y, w, h,
                    Win32.SWP_FRAMECHANGED | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
                Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
                Active = true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                Active = false;
            }
#endif
        }

        /// <summary>커서가 캐릭터 위에 있을 때 false(클릭 받음), 아니면 true(클릭 통과).</summary>
        public void SetClickThrough(bool through)
        {
            if (!Active || through == _clickThrough) return;
            uint ex = Win32.GetStyle(Hwnd, Win32.GWL_EXSTYLE);
            ex = through ? (ex | Win32.WS_EX_TRANSPARENT) : (ex & ~Win32.WS_EX_TRANSPARENT);
            Win32.SetStyle(Hwnd, Win32.GWL_EXSTYLE, ex);
            _clickThrough = through;
        }

        /// <summary>화면 전체 기준 커서 위치(물리 px).</summary>
        public bool TryGetCursorScreen(out Vector2Int p)
        {
            p = Vector2Int.zero;
            if (!Active) return false;
            Win32.POINT pt;
            if (!Win32.GetCursorPos(out pt)) return false;
            p = new Vector2Int(pt.X, pt.Y);
            return true;
        }

        /// <summary>창 안 좌표(물리 px, Unity 방식: 왼쪽 아래가 0). 창 밖이면 inside=false.</summary>
        public bool TryGetCursorClient(out Vector2 client, out bool inside)
        {
            client = Vector2.zero;
            inside = false;
            if (!Active) return false;
            Win32.POINT pt;
            Win32.RECT r;
            if (!Win32.GetCursorPos(out pt) || !Win32.GetWindowRect(Hwnd, out r)) return false;
            float cx = pt.X - r.Left;
            float cy = pt.Y - r.Top;
            client = new Vector2(cx, r.Height - cy);
            inside = cx >= 0 && cy >= 0 && cx < r.Width && cy < r.Height;
            return true;
        }

        public bool TryGetWindowPos(out Vector2Int pos)
        {
            pos = Vector2Int.zero;
            if (!Active) return false;
            Win32.RECT r;
            if (!Win32.GetWindowRect(Hwnd, out r)) return false;
            pos = new Vector2Int(r.Left, r.Top);
            return true;
        }

        /// <summary>창을 옮긴다(화면 경계 안으로 제한).</summary>
        public void MoveTo(int x, int y)
        {
            if (!Active) return;
            Win32.RECT r;
            if (!Win32.GetWindowRect(Hwnd, out r)) return;
            Clamp(ref x, ref y, r.Width, r.Height);
            Win32.SetWindowPos(Hwnd, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }

        public void ResetPosition()
        {
            if (!Active) return;
            _work = ReadWorkArea();
            int w, h, x, y;
            PhysicalSize(out w, out h);
            DefaultPosition(w, h, out x, out y);
            Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE);
        }

        /// <summary>저장용: 작업영역 오른쪽/아래 끝에서 창까지의 거리(논리 px). 해상도·배율이 바뀌어도 같은 구석을 유지.</summary>
        public bool TryGetSavePosition(out double fromRight, out double fromBottom)
        {
            fromRight = fromBottom = 0;
            if (!Active) return false;
            Win32.RECT r;
            if (!Win32.GetWindowRect(Hwnd, out r)) return false;
            fromRight = (_work.Right - r.Right) / (double)Scale;
            fromBottom = (_work.Bottom - r.Bottom) / (double)Scale;
            return true;
        }

        /// <summary>주기적으로 호출: 항상 위 유지 + 해상도/배율/작업표시줄 변경 시 크기·위치 복구.</summary>
        public void Maintain(float deltaTime)
        {
            if (!Active) return;
            _topmostTimer += deltaTime;
            if (_topmostTimer < 2f) return;
            _topmostTimer = 0f;

            Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

            var work = ReadWorkArea();
            uint dpi = ReadDpi();
            bool changed = work.Left != _work.Left || work.Top != _work.Top || work.Right != _work.Right || work.Bottom != _work.Bottom || dpi != _dpi;
            if (!changed) return;

            double fromRight, fromBottom;
            bool hadPos = TryGetSavePosition(out fromRight, out fromBottom);
            _work = work;
            _dpi = dpi;
            Scale = dpi / 96f;

            int w, h, x, y;
            PhysicalSize(out w, out h);
            if (hadPos)
            {
                x = (int)Math.Round(_work.Right - fromRight * Scale - w);
                y = (int)Math.Round(_work.Bottom - fromBottom * Scale - h);
            }
            else DefaultPosition(w, h, out x, out y);
            Clamp(ref x, ref y, w, h);
            Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE);
        }

        /// <summary>(선택 기능) 마지막 키보드/마우스 입력 이후 경과 초. 읽지 못하면 null(= 알 수 없음).</summary>
        public double? UserIdleSeconds()
        {
            if (!Active) return null;
            try
            {
                var info = new Win32.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.LASTINPUTINFO)) };
                if (!Win32.GetLastInputInfo(ref info)) return null;
                uint elapsed = unchecked(Win32.GetTickCount() - info.dwTime);
                return elapsed / 1000.0;
            }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------

        private void PhysicalSize(out int w, out int h)
        {
            w = Mathf.RoundToInt(LogicalWidth * Scale);
            h = Mathf.RoundToInt(LogicalHeight * Scale);
        }

        private void DefaultPosition(int w, int h, out int x, out int y)
        {
            x = _work.Right - w - Mathf.RoundToInt(16 * Scale);
            y = _work.Bottom - h;
        }

        private void Clamp(ref int x, ref int y, int w, int h)
        {
            if (x > _work.Right - w) x = _work.Right - w;
            if (y > _work.Bottom - h) y = _work.Bottom - h;
            if (x < _work.Left) x = _work.Left;
            if (y < _work.Top) y = _work.Top;
        }

        private static Win32.RECT ReadWorkArea()
        {
            var r = new Win32.RECT();
            if (!Win32.SystemParametersInfo(Win32.SPI_GETWORKAREA, 0, ref r, 0) || r.Width <= 0)
            {
                r.Left = 0; r.Top = 0;
                r.Right = Display.main.systemWidth;
                r.Bottom = Display.main.systemHeight;
            }
            return r;
        }

        private uint ReadDpi()
        {
            try
            {
                uint d = Win32.GetDpiForWindow(Hwnd);
                return d == 0 ? 96u : d;
            }
            catch (EntryPointNotFoundException)
            {
                return 96u;
            }
        }

        private static IntPtr FindMainWindow()
        {
            _found = IntPtr.Zero;
            _myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            Win32.EnumWindows(EnumCallback, IntPtr.Zero);
            return _found;
        }

        [AOT.MonoPInvokeCallback(typeof(Win32.EnumWindowsProc))]
        private static bool OnEnumWindow(IntPtr hWnd, IntPtr lParam)
        {
            uint pid;
            Win32.GetWindowThreadProcessId(hWnd, out pid);
            if (pid != _myPid) return true;
            var sb = new StringBuilder(64);
            Win32.GetClassName(hWnd, sb, sb.Capacity);
            if (sb.ToString() == "UnityWndClass")
            {
                _found = hWnd;
                return false;
            }
            return true;
        }
    }
}
