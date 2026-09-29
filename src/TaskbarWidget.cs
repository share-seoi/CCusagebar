using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexUsageBar
{
    /// Windows 11 작업표시줄(Shell_TrayWnd) 안, 알림 영역(숨겨진 아이콘 ^) 바로 왼쪽에 붙는 자식 창.
    /// 픽셀 단위 알파(UpdateLayeredWindow)로 그려서 작업표시줄 배경이 그대로 비친다.
    /// 탐색기가 다시 시작되면 작업표시줄 창이 바뀌므로 1초마다 위치와 부모를 확인해 다시 붙는다.
    internal sealed class TaskbarWidget : NativeWindow, IDisposable
    {
        public event EventHandler LeftClick;
        public event EventHandler RightClick;

        private const float MarginRight = 4f;

        private readonly Timer layoutTimer;
        private readonly TooltipWindow tooltip = new TooltipWindow();
        private WidgetContent content = new WidgetContent { Tooltip = "Codex Usage Bar" };
        private IntPtr trayHandle;
        private Rectangle placedBounds;
        private Rectangle screenBounds;
        private bool shown;
        private bool wantVisible;
        private bool hover;
        private bool dark;
        private float scale = 1f;
        internal string LastAttachError { get; private set; }

        public TaskbarWidget()
        {
            layoutTimer = new Timer { Interval = 1000 };
            layoutTimer.Tick += delegate { UpdateLayout(false); };
            layoutTimer.Start();
        }

        /// 화면 좌표 기준 위젯 위치(상세 창 배치용).
        public Rectangle ScreenBounds
        {
            get { return screenBounds; }
        }

        public float Scale
        {
            get { return scale; }
        }

        public bool IsDark
        {
            get { return dark; }
        }

        public IntPtr TrayHandle
        {
            get { return trayHandle; }
        }

        public void SetVisible(bool visible)
        {
            wantVisible = visible;
            UpdateLayout(true);
        }

        public void SetContent(WidgetContent newContent)
        {
            bool widthChanged = BatteryRenderer.Width(scale, newContent) != BatteryRenderer.Width(scale, content);
            content = newContent;
            // 배터리나 앱 개수가 바뀌면 위젯 너비도 바뀌므로 위치를 다시 잡는다.
            if (widthChanged) UpdateLayout(true);
            else Render();
            tooltip.SetText(content.Tooltip);
        }

        public void HideTooltip()
        {
            tooltip.Pop();
        }

        private void UpdateLayout(bool force)
        {
            var tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero)
            {
                Detach();
                return;
            }
            if (tray != trayHandle || Handle == IntPtr.Zero || !Native.IsWindow(Handle))
            {
                Detach();
                trayHandle = tray;
                Attach();
                force = true;
            }
            if (Handle == IntPtr.Zero) return;

            Native.RECT trayRect;
            if (!Native.GetWindowRect(tray, out trayRect)) return;

            var newScale = Math.Max(1f, Native.GetDpiForWindow(tray) / 96f);
            var newDark = Theme.SystemIsDark();
            bool appearanceChanged = newScale != scale || newDark != dark;
            scale = newScale;
            dark = newDark;

            int width = BatteryRenderer.Width(scale, content);
            int height = trayRect.Height;
            int anchorRight = trayRect.Right - (int)(260 * scale);
            var notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            Native.RECT notifyRect;
            if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out notifyRect) && notifyRect.Width > 0)
            {
                anchorRight = notifyRect.Left;
            }
            int screenX = anchorRight - width - (int)(MarginRight * scale);
            screenBounds = new Rectangle(screenX, trayRect.Top, width, height);

            var client = new Native.POINT(screenX, trayRect.Top);
            Native.ScreenToClient(tray, ref client);
            var bounds = new Rectangle(client.X, client.Y, width, height);

            bool visible = wantVisible && Native.IsWindowVisible(tray);
            if (!visible)
            {
                if (shown)
                {
                    Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                        Native.SWP_HIDEWINDOW | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    shown = false;
                }
                return;
            }

            if (force || !shown || bounds != placedBounds)
            {
                Native.SetWindowPos(Handle, Native.HWND_TOP, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                placedBounds = bounds;
                shown = true;
                Render();
            }
            else if (appearanceChanged)
            {
                Render();
            }
        }

        private void Attach()
        {
            var cp = new CreateParams
            {
                Caption = "CodexUsageBar",
                Style = Native.WS_CHILD | Native.WS_CLIPSIBLINGS,
                ExStyle = Native.WS_EX_LAYERED,
                Parent = trayHandle,
                Width = 1,
                Height = 1
            };
            try
            {
                CreateHandle(cp);
                LastAttachError = null;
                tooltip.Attach(Handle);
                tooltip.SetText(content.Tooltip);
            }
            catch (Exception error)
            {
                LastAttachError = error.Message;
                // 작업표시줄이 아직 준비되지 않았으면 다음 확인 때 다시 시도한다.
            }
            shown = false;
            placedBounds = Rectangle.Empty;
        }

        private void Detach()
        {
            tooltip.Detach();
            if (Handle != IntPtr.Zero)
            {
                try
                {
                    DestroyHandle();
                }
                catch (Exception)
                {
                    ReleaseHandle();
                }
            }
            trayHandle = IntPtr.Zero;
            shown = false;
        }

        private void Render()
        {
            if (Handle == IntPtr.Zero || placedBounds.Width <= 0 || placedBounds.Height <= 0) return;

            using (var bitmap = BatteryRenderer.Render(content, placedBounds.Size, scale, dark, hover))
            {
                IntPtr screenDc = Native.GetDC(IntPtr.Zero);
                IntPtr memoryDc = Native.CreateCompatibleDC(screenDc);
                IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                IntPtr old = Native.SelectObject(memoryDc, hBitmap);
                try
                {
                    var size = new Native.SIZE(bitmap.Width, bitmap.Height);
                    var source = new Native.POINT(0, 0);
                    var blend = new Native.BLENDFUNCTION
                    {
                        BlendOp = 0,
                        BlendFlags = 0,
                        SourceConstantAlpha = 255,
                        AlphaFormat = Native.AC_SRC_ALPHA
                    };
                    Native.UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memoryDc, ref source, 0, ref blend, Native.ULW_ALPHA);
                }
                finally
                {
                    Native.SelectObject(memoryDc, old);
                    Native.DeleteObject(hBitmap);
                    Native.DeleteDC(memoryDc);
                    Native.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_MOUSEACTIVATE:
                    // 클릭해도 작업표시줄이 활성화되지 않게 해서, 앞에 있던 앱(Codex/Claude)이 그대로 유지되게 한다.
                    m.Result = (IntPtr)Native.MA_NOACTIVATE;
                    return;
                case Native.WM_MOUSEMOVE:
                    if (!hover)
                    {
                        hover = true;
                        var track = new Native.TRACKMOUSEEVENT
                        {
                            cbSize = Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT)),
                            dwFlags = Native.TME_LEAVE,
                            hwndTrack = Handle
                        };
                        Native.TrackMouseEvent(ref track);
                        Render();
                    }
                    break;
                case Native.WM_MOUSELEAVE:
                    hover = false;
                    Render();
                    break;
                case Native.WM_LBUTTONUP:
                    tooltip.Pop();
                    Raise(LeftClick);
                    break;
                case Native.WM_RBUTTONUP:
                    tooltip.Pop();
                    Raise(RightClick);
                    break;
            }
            base.WndProc(ref m);
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            layoutTimer.Dispose();
            Detach();
        }
    }

    /// 위젯에 마우스를 올리면 뜨는 기본 풍선 도움말. 위젯은 활성 창이 아니므로 TTS_ALWAYSTIP를 쓴다.
    internal sealed class TooltipWindow : NativeWindow
    {
        private const int TTS_ALWAYSTIP = 0x01;
        private const int TTS_NOPREFIX = 0x02;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int TTF_IDISHWND = 0x0001;
        private const int TTF_SUBCLASS = 0x0010;
        private const int TTM_ADDTOOLW = 0x0432;
        private const int TTM_DELTOOLW = 0x0433;
        private const int TTM_UPDATETIPTEXTW = 0x0439;
        private const int TTM_SETMAXTIPWIDTH = 0x0418;
        private const int TTM_POP = 0x041C;

        [StructLayout(LayoutKind.Sequential)]
        private struct TOOLINFO
        {
            public int cbSize;
            public int uFlags;
            public IntPtr hwnd;
            public IntPtr uId;
            public Native.RECT rect;
            public IntPtr hinst;
            public IntPtr lpszText;
            public IntPtr lParam;
            public IntPtr lpReserved;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TOOLINFO lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private IntPtr tool;
        private string text = "";

        public void Attach(IntPtr target)
        {
            Detach();
            CreateHandle(new CreateParams
            {
                ClassName = "tooltips_class32",
                Style = WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX,
                // 위젯의 최상위 창은 탐색기 소유라, 소유자 없이 띄운다.
                ExStyle = WS_EX_TOPMOST
            });
            tool = target;
            SendMessage(Handle, TTM_SETMAXTIPWIDTH, IntPtr.Zero, (IntPtr)600);
            Send(TTM_ADDTOOLW);
        }

        public void Detach()
        {
            if (Handle == IntPtr.Zero) return;
            try
            {
                DestroyHandle();
            }
            catch (Exception)
            {
                ReleaseHandle();
            }
            tool = IntPtr.Zero;
        }

        public void SetText(string value)
        {
            text = value ?? "";
            if (Handle != IntPtr.Zero) Send(TTM_UPDATETIPTEXTW);
        }

        public void Pop()
        {
            if (Handle != IntPtr.Zero) SendMessage(Handle, TTM_POP, IntPtr.Zero, IntPtr.Zero);
        }

        private void Send(int message)
        {
            var textPointer = Marshal.StringToHGlobalUni(text);
            try
            {
                var info = new TOOLINFO
                {
                    cbSize = Marshal.SizeOf(typeof(TOOLINFO)),
                    uFlags = TTF_IDISHWND | TTF_SUBCLASS,
                    hwnd = tool,
                    uId = tool,
                    lpszText = textPointer
                };
                SendMessage(Handle, message, IntPtr.Zero, ref info);
            }
            finally
            {
                Marshal.FreeHGlobal(textPointer);
            }
        }
    }
}
