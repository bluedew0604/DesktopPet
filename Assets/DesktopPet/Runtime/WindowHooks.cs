using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace DesktopPet
{
    public enum HookEvent { MouseDown, MouseUp, CaptureLost }

    /// <summary>
    /// Unity 창의 메시지를 가로채서 (1) 마우스 누름/뗌을 하나도 빠짐없이 기록하고,
    /// (2) 클릭해도 창이 활성화되지 않게 한다(포커스 안 뺏기).
    /// 프레임마다 마우스 상태를 훑는 방식은 아주 짧은 클릭을 놓칠 수 있어서 메시지로 받는다.
    /// (Unity 윈도우 플레이어는 창 메시지를 게임 메인 스레드와 다른 스레드에서 처리하므로 큐는 잠금으로 보호)
    /// </summary>
    public static class WindowHooks
    {
        private static IntPtr _hwnd;
        private static IntPtr _oldProc;
        private static bool _capturing;
        private static readonly Win32.WndProc Proc = HookProc;
        private static readonly Queue<HookEvent> Events = new Queue<HookEvent>();

        public static bool Installed { get { return _oldProc != IntPtr.Zero; } }

        public static bool Install(IntPtr hwnd)
        {
            if (Installed || hwnd == IntPtr.Zero) return Installed;
            _hwnd = hwnd;
            IntPtr fn = Marshal.GetFunctionPointerForDelegate(Proc);
            _oldProc = Win32.SetWindowLongPtr(hwnd, Win32.GWLP_WNDPROC, fn);
            return _oldProc != IntPtr.Zero;
        }

        public static void Uninstall()
        {
            if (!Installed) return;
            _capturing = false;
            Win32.SetWindowLongPtr(_hwnd, Win32.GWLP_WNDPROC, _oldProc);
            _oldProc = IntPtr.Zero;
        }

        public static bool TryDequeue(out HookEvent e)
        {
            lock (Events)
            {
                if (Events.Count > 0) { e = Events.Dequeue(); return true; }
            }
            e = HookEvent.MouseUp;
            return false;
        }

        /// <summary>
        /// 게임 쪽에서 누름 상태를 끝냈을 때 호출. 마우스 캡처는 창 스레드 소유라 여기서 풀 수 없고,
        /// 버튼을 떼는 순간 창 스레드(HookProc)에서 자동으로 풀린다.
        /// </summary>
        public static void EndCapture()
        {
        }

        private static void Push(HookEvent e)
        {
            lock (Events)
            {
                while (Events.Count >= 256) Events.Dequeue(); // 가득 차면 가장 오래된 것부터 버림
                Events.Enqueue(e);
            }
        }

        [AOT.MonoPInvokeCallback(typeof(Win32.WndProc))]
        private static IntPtr HookProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == Win32.WM_MOUSEACTIVATE)
                    return new IntPtr(Win32.MA_NOACTIVATE);

                if (msg == Win32.WM_LBUTTONDOWN || msg == Win32.WM_LBUTTONDBLCLK)
                {
                    Push(HookEvent.MouseDown);
                    _capturing = true;
                    Win32.SetCapture(hWnd); // 창 밖으로 빠르게 끌어도 뗌을 놓치지 않게
                }
                else if (msg == Win32.WM_LBUTTONUP)
                {
                    Push(HookEvent.MouseUp);
                    if (_capturing) { _capturing = false; Win32.ReleaseCapture(); }
                }
                else if (msg == Win32.WM_CAPTURECHANGED)
                {
                    if (_capturing) { _capturing = false; Push(HookEvent.CaptureLost); }
                }
            }
            catch (Exception)
            {
                // 메시지 처리 중 예외가 Unity 쪽으로 새어나가지 않게 한다.
            }
            return Win32.CallWindowProc(_oldProc, hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>
    /// 작업표시줄 알림 영역(트레이) 아이콘 + 오른쪽 클릭 메뉴.
    /// Unity 창과 완전히 분리된 "숨은 전용 창"을 전용 스레드에서 만들고 그 스레드에서 메시지를 돌린다.
    /// (일반 윈도우 트레이 프로그램과 같은 방식 — Unity 창의 스레드/스타일 영향을 받지 않음)
    /// 게임 쪽(메인 스레드)과는 잠금으로 보호된 큐로만 주고받는다.
    /// </summary>
    public sealed class TrayIcon
    {
        public struct MenuItem
        {
            public int Id;          // 0 = 구분선
            public string Text;
            public bool Checked;
            public bool Disabled;
        }

        private const uint CallbackMessage = Win32.WM_APP + 1;
        private const uint ReplaceIconMessage = Win32.WM_APP + 2;
        private const string ClassName = "DesktopPetTrayWindow";

        private static TrayIcon _instance; // 창 프로시저(정적)에서 찾기 위함
        private static readonly Win32.WndProc Proc = TrayProc;

        private readonly object _lock = new object();
        private readonly Queue<int> _commands = new Queue<int>();
        private int _leftClicks;
        private MenuItem[] _menu;
        private string _icoPath, _pendingIcoPath, _tip;

        private Thread _thread;
        private IntPtr _hwnd;
        private IntPtr _icon;
        private bool _ownsIcon;
        private bool _added;
        private uint _taskbarCreated;
        private volatile bool _started;

        public bool Running { get { return _started; } }

        // ---------------- 게임(메인) 스레드에서 호출 ----------------

        public void Start(string icoPath, string tip)
        {
            if (_thread != null) return;
            _instance = this;
            _icoPath = icoPath;
            _tip = tip;
            _thread = new Thread(ThreadMain) { IsBackground = true, Name = "DesktopPet Tray" };
            _thread.Start();
        }

        public void SetMenu(MenuItem[] items)
        {
            lock (_lock) _menu = items;
        }

        public bool TryDequeueCommand(out int id)
        {
            lock (_lock)
            {
                if (_commands.Count > 0) { id = _commands.Dequeue(); return true; }
            }
            id = 0;
            return false;
        }

        public bool TryConsumeLeftClick()
        {
            lock (_lock)
            {
                if (_leftClicks > 0) { _leftClicks--; return true; }
            }
            return false;
        }

        public void ReplaceIcon(string icoPath)
        {
            lock (_lock) _pendingIcoPath = icoPath;
            if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, ReplaceIconMessage, IntPtr.Zero, IntPtr.Zero);
        }

        public void Stop()
        {
            if (_thread == null) return;
            if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1500);
            _thread = null;
        }

        // ---------------- 트레이 전용 스레드 ----------------

        private void ThreadMain()
        {
            try
            {
                IntPtr hInst = Win32.GetModuleHandle(null);
                var wc = new Win32.WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(Win32.WNDCLASSEX)),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                    hInstance = hInst,
                    lpszClassName = ClassName
                };
                ushort atom = Win32.RegisterClassEx(ref wc);
                if (atom == 0) Debug.LogWarning("[DesktopPet] 트레이 창 클래스 등록 실패 오류=" + Marshal.GetLastWin32Error());

                _hwnd = Win32.CreateWindowEx(0, ClassName, "DesktopPet Tray", Win32.WS_POPUP, 0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                {
                    Debug.LogWarning("[DesktopPet] 트레이 창 만들기 실패 오류=" + Marshal.GetLastWin32Error());
                    return;
                }

                _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
                LoadIconFile(_icoPath);
                AddIcon();
                _started = true;
                Debug.Log("[DesktopPet] 트레이 준비됨 (아이콘 등록=" + _added + ")");

                Win32.MSG msg;
                while (Win32.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Win32.TranslateMessage(ref msg);
                    Win32.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DesktopPet] 트레이 스레드 오류: " + ex);
            }
            finally
            {
                RemoveIcon();
                if (_hwnd != IntPtr.Zero) { Win32.DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
                Win32.UnregisterClass(ClassName, Win32.GetModuleHandle(null));
                _started = false;
            }
        }

        [AOT.MonoPInvokeCallback(typeof(Win32.WndProc))]
        private static IntPtr TrayProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var self = _instance;
            try
            {
                if (self != null)
                {
                    if (msg == CallbackMessage)
                    {
                        uint mouseMsg = unchecked((uint)(lParam.ToInt64() & 0xFFFF));
                        if (mouseMsg != 0x0200) // 마우스 이동(0x200)은 너무 많아서 기록 안 함
                            Debug.Log("[DesktopPet] 트레이 신호 0x" + mouseMsg.ToString("X"));
                        // 왼쪽·오른쪽 클릭 모두 메뉴를 띄운다 (일기는 메뉴의 '오늘 일기 보기')
                        if (mouseMsg == Win32.WM_LBUTTONUP || mouseMsg == Win32.WM_RBUTTONUP || mouseMsg == Win32.WM_CONTEXTMENU)
                            self.ShowMenu(hWnd);
                        return IntPtr.Zero;
                    }
                    if (msg == ReplaceIconMessage)
                    {
                        string path;
                        lock (self._lock) { path = self._pendingIcoPath; self._pendingIcoPath = null; }
                        if (path != null) self.SwapIcon(path);
                        return IntPtr.Zero;
                    }
                    if (self._taskbarCreated != 0 && msg == self._taskbarCreated)
                    {
                        self._added = false;
                        self.AddIcon(); // 탐색기가 재시작되면 아이콘이 사라지므로 다시 등록
                        return IntPtr.Zero;
                    }
                }
                if (msg == Win32.WM_CLOSE)
                {
                    Win32.DestroyWindow(hWnd);
                    return IntPtr.Zero;
                }
                if (msg == Win32.WM_DESTROY)
                {
                    if (self != null) self.RemoveIcon();
                    Win32.PostQuitMessage(0);
                    return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DesktopPet] 트레이 메시지 처리 오류: " + ex.Message);
            }
            return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void ShowMenu(IntPtr hWnd)
        {
            MenuItem[] items;
            lock (_lock) items = _menu;
            if (items == null) return;

            IntPtr menu = Win32.CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                foreach (var it in items)
                {
                    if (it.Id == 0) { Win32.AppendMenu(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null); continue; }
                    uint flags = Win32.MF_STRING;
                    if (it.Checked) flags |= Win32.MF_CHECKED;
                    if (it.Disabled) flags |= Win32.MF_GRAYED;
                    Win32.AppendMenu(menu, flags, new UIntPtr((uint)it.Id), it.Text);
                }
                Win32.POINT p;
                Win32.GetCursorPos(out p);
                bool fg = Win32.SetForegroundWindow(hWnd); // 이게 없으면 메뉴 밖을 눌러도 메뉴가 안 닫힌다
                Debug.Log("[DesktopPet] 트레이 메뉴 띄움 (전면=" + fg + ", 위치=" + p.X + "," + p.Y + ")");
                int cmd = Win32.TrackPopupMenuEx(menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY | Win32.TPM_BOTTOMALIGN, p.X, p.Y, hWnd, IntPtr.Zero);
                int err = cmd == 0 ? Marshal.GetLastWin32Error() : 0;
                Win32.PostMessage(hWnd, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
                Debug.Log("[DesktopPet] 트레이 메뉴 결과=" + cmd + (err != 0 ? " 오류=" + err : ""));
                if (cmd != 0)
                {
                    lock (_lock)
                    {
                        while (_commands.Count >= 32) _commands.Dequeue();
                        _commands.Enqueue(cmd);
                    }
                }
            }
            finally
            {
                Win32.DestroyMenu(menu);
            }
        }

        private void AddIcon()
        {
            var data = MakeData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP);
            _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data);
        }

        private void RemoveIcon()
        {
            if (_added)
            {
                var data = MakeData(0);
                Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
                _added = false;
            }
            if (_ownsIcon && _icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
            _ownsIcon = false;
        }

        private void SwapIcon(string icoPath)
        {
            IntPtr old = _icon;
            bool ownedOld = _ownsIcon;
            LoadIconFile(icoPath);
            var data = MakeData(Win32.NIF_ICON);
            Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
            if (ownedOld && old != IntPtr.Zero && old != _icon) Win32.DestroyIcon(old);
        }

        private void LoadIconFile(string icoPath)
        {
            _icon = IntPtr.Zero;
            _ownsIcon = false;
            if (!string.IsNullOrEmpty(icoPath) && File.Exists(icoPath))
            {
                int size = Win32.GetSystemMetrics(Win32.SM_CXSMICON);
                if (size <= 0) size = 16;
                _icon = Win32.LoadImage(IntPtr.Zero, icoPath, Win32.IMAGE_ICON, size, size, Win32.LR_LOADFROMFILE);
                _ownsIcon = _icon != IntPtr.Zero;
            }
            if (_icon == IntPtr.Zero) _icon = Win32.LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
        }

        private Win32.NOTIFYICONDATAW MakeData(uint flags)
        {
            var d = new Win32.NOTIFYICONDATAW
            {
                hWnd = _hwnd,
                uID = 1,
                uFlags = flags,
                uCallbackMessage = CallbackMessage,
                hIcon = _icon,
                szTip = _tip ?? "",
                szInfo = "",
                szInfoTitle = ""
            };
            d.cbSize = (uint)Marshal.SizeOf(typeof(Win32.NOTIFYICONDATAW));
            return d;
        }
    }

    /// <summary>텍스처로 .ico 파일을 만든다(여러 크기, 32비트 알파). 트레이 아이콘용.</summary>
    public static class IcoWriter
    {
        public static byte[] Build(Texture2D src, Rect srcRect, int[] sizes)
        {
            var images = new List<byte[]>();
            foreach (int s in sizes) images.Add(BuildBitmap(src, srcRect, s));

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int s = sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static byte[] BuildBitmap(Texture2D src, Rect r, int size)
        {
            int maskStride = ((size + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40); w.Write(size); w.Write(size * 2);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write(0); w.Write(size * size * 4 + maskStride * size);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);

                // 정사각형 안에 비율 유지해서 맞춤
                float side = Mathf.Max(r.width, r.height);
                float ox = r.x - (side - r.width) / 2f;
                float oy = r.y - (side - r.height) / 2f;
                for (int y = 0; y < size; y++) // 아래쪽 줄부터
                {
                    for (int x = 0; x < size; x++)
                    {
                        float u = (ox + (x + 0.5f) / size * side) / src.width;
                        float v = (oy + (y + 0.5f) / size * side) / src.height;
                        Color c = (u < 0 || v < 0 || u > 1 || v > 1) ? Color.clear : src.GetPixelBilinear(u, v);
                        w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255));
                        w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255));
                        w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255));
                        w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(c.a) * 255));
                    }
                }
                w.Write(new byte[maskStride * size]);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
