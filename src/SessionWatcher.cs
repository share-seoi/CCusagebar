using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace CCusagebar
{
    /// 로그오프·종료나 설치 프로그램(Restart Manager)의 종료 요청을 받는 숨은 최상위 창.
    /// 요청을 받고도 끝나지 않으면 Windows가 "응답 없음"으로 강제 종료하므로 바로 끝낸다.
    /// 다른 앱 업데이트 때문에 닫힌 경우(로그오프·종료가 아님)에는 잠시 뒤 다시 켜지도록 예약한다.
    internal sealed class SessionWatcher : NativeWindow, IDisposable
    {
        public const string DelayedStartArgument = "--delayed-start";
        internal const int RelaunchDelaySeconds = 20;
        private const string WindowCaption = AppInfo.Name + ".Session";
        // 바로가기로 한 번 더 실행하면 이미 켜진 위젯에 이 메시지를 보내 끈다.
        private const int WM_APP_QUIT = 0x8000 + 1;

        private const int WM_QUERYENDSESSION = 0x0011;
        private const int WM_ENDSESSION = 0x0016;
        private const long ENDSESSION_CLOSEAPP = 0x1;
        private const long ENDSESSION_LOGOFF = 0x80000000;

        public SessionWatcher()
        {
            // 메시지 전용 창은 종료 알림을 못 받으므로 보이지 않는 일반 최상위 창을 만든다.
            CreateHandle(new CreateParams { Caption = WindowCaption });
        }

        /// 이미 실행 중인 위젯에 종료를 요청한다. 찾지 못하면 false.
        public static bool RequestQuitOfRunningInstance()
        {
            var window = Native.FindWindow(null, WindowCaption);
            return window != IntPtr.Zero && Native.PostMessage(window, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_APP_QUIT)
            {
                Log.Write("바로가기를 다시 눌러 종료");
                Application.Exit();
            }
            else if (m.Msg == WM_QUERYENDSESSION)
            {
                Log.Write("종료 요청 받음 (" + Reason(m.LParam) + ")");
            }
            else if (m.Msg == WM_ENDSESSION && m.WParam != IntPtr.Zero)
            {
                long flags = m.LParam.ToInt64();
                bool appClose = IsAppCloseOnly(flags);
                Log.Write("세션 종료로 위젯을 닫음 (" + Reason(m.LParam) + ")"
                    + (appClose ? " · " + RelaunchDelaySeconds + "초 뒤 다시 실행 예약" : ""));
                if (appClose) ScheduleRelaunch();
                Application.Exit();
            }
            else if (m.Msg == WM_ENDSESSION)
            {
                Log.Write("종료 요청이 취소됨");
            }
            base.WndProc(ref m);
        }

        /// 로그오프나 Windows 종료 없이 이 앱만 닫으라는 요청인가(예: 다른 앱 업데이트).
        internal static bool IsAppCloseOnly(long flags)
        {
            return (flags & ENDSESSION_CLOSEAPP) != 0 && (flags & ENDSESSION_LOGOFF) == 0;
        }

        private static string Reason(IntPtr lParam)
        {
            long flags = lParam.ToInt64();
            if ((flags & ENDSESSION_LOGOFF) != 0) return "로그오프";
            if ((flags & ENDSESSION_CLOSEAPP) != 0) return "다른 프로그램의 업데이트·설치가 닫기 요청";
            return "Windows 종료·다시 시작";
        }

        private static void ScheduleRelaunch()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, DelayedStartArgument + " " + RelaunchDelaySeconds)
                {
                    UseShellExecute = false
                });
            }
            catch (Exception error)
            {
                Log.Error("다시 실행 예약 실패", error);
            }
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }
}
