using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace DesktopPet
{
    public enum HookEvent { MouseDown, MouseUp, CaptureLost, TrayLeftClick, TrayRightClick, TaskbarRestarted }

    /// <summary>
    /// Unity 창의 메시지를 가로채서 (1) 마우스 누름/뗌을 하나도 빠짐없이 기록하고,
    /// (2) 클릭해도 창이 활성화되지 않게 하고(포커스 안 뺏기), (3) 트레이 아이콘 클릭을 받는다.
    /// 프레임마다 마우스 상태를 훑는 방식은 아주 짧은 클릭을 놓칠 수 있어서 메시지로 받는다.
    /// </summary>
    public static class WindowHooks
    {
        public const uint TrayCallbackMessage = Win32.WM_APP + 1;

        private static IntPtr _hwnd;
        private static IntPtr _oldProc;
        private static uint _taskbarCreated;
        private static bool _capturing;
        private static readonly Win32.WndProc Proc = HookProc;
        private static readonly Queue<HookEvent> Events = new Queue<HookEvent>();

        public static bool Installed { get { return _oldProc != IntPtr.Zero; } }

        public static bool Install(IntPtr hwnd)
        {
            if (Installed || hwnd == IntPtr.Zero) return Installed;
            _hwnd = hwnd;
            _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
            IntPtr fn = Marshal.GetFunctionPointerForDelegate(Proc);
            _oldProc = Win32.SetWindowLongPtr(hwnd, Win32.GWLP_WNDPROC, fn);
            return _oldProc != IntPtr.Zero;
        }

        public static void Uninstall()
        {
            if (!Installed) return;
            if (_capturing) { _capturing = false; Win32.ReleaseCapture(); }
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

        public static void EndCapture()
        {
            if (!_capturing) return;
            _capturing = false;
            Win32.ReleaseCapture();
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
                else if (msg == TrayCallbackMessage)
                {
                    uint mouseMsg = unchecked((uint)(lParam.ToInt64() & 0xFFFF));
                    if (mouseMsg == Win32.WM_LBUTTONUP) Push(HookEvent.TrayLeftClick);
                    else if (mouseMsg == Win32.WM_RBUTTONUP || mouseMsg == Win32.WM_CONTEXTMENU) Push(HookEvent.TrayRightClick);
                    return IntPtr.Zero;
                }
                else if (_taskbarCreated != 0 && msg == _taskbarCreated)
                {
                    Push(HookEvent.TaskbarRestarted);
                }
            }
            catch (Exception)
            {
                // 메시지 처리 중 예외가 Unity 쪽으로 새어나가지 않게 한다.
            }
            return Win32.CallWindowProc(_oldProc, hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>작업표시줄 알림 영역(트레이) 아이콘 + 오른쪽 클릭 메뉴.</summary>
    public sealed class TrayIcon
    {
        public struct MenuItem
        {
            public int Id;          // 0 = 구분선
            public string Text;
            public bool Checked;
            public bool Disabled;
        }

        private IntPtr _hwnd;
        private IntPtr _icon;
        private bool _ownsIcon;
        private bool _added;
        private string _tip;

        public bool Added { get { return _added; } }

        public bool Add(IntPtr hwnd, string icoPath, string tip)
        {
            _hwnd = hwnd;
            _tip = tip;
            LoadIconFile(icoPath);
            var data = MakeData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP);
            _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data);
            return _added;
        }

        /// <summary>탐색기가 재시작되면 아이콘이 사라지므로 다시 등록.</summary>
        public void Readd()
        {
            if (_hwnd == IntPtr.Zero) return;
            var data = MakeData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP);
            _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data);
        }

        public void ReplaceIcon(string icoPath)
        {
            if (!_added) return;
            IntPtr old = _icon;
            bool ownedOld = _ownsIcon;
            LoadIconFile(icoPath);
            var data = MakeData(Win32.NIF_ICON);
            Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
            if (ownedOld && old != IntPtr.Zero && old != _icon) Win32.DestroyIcon(old);
        }

        public void Remove()
        {
            if (_added)
            {
                var data = MakeData(0);
                Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
                _added = false;
            }
            if (_ownsIcon && _icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        /// <summary>커서 위치에 메뉴를 띄우고 고른 항목 Id를 돌려준다(취소하면 0). 메뉴가 떠 있는 동안은 멈춘다.</summary>
        public int ShowMenu(IList<MenuItem> items)
        {
            IntPtr menu = Win32.CreatePopupMenu();
            if (menu == IntPtr.Zero) return 0;
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
                Win32.SetForegroundWindow(_hwnd); // 이게 없으면 메뉴 밖을 눌러도 메뉴가 안 닫힌다
                int cmd = Win32.TrackPopupMenuEx(menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY | Win32.TPM_BOTTOMALIGN, p.X, p.Y, _hwnd, IntPtr.Zero);
                Win32.PostMessage(_hwnd, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
                return cmd;
            }
            finally
            {
                Win32.DestroyMenu(menu);
            }
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
                uCallbackMessage = WindowHooks.TrayCallbackMessage,
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
