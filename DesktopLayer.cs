using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DesktopTodo
{
    internal sealed class DesktopLayer : IDisposable
    {
        private const uint PlaceFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200 | 0x0400;
        private static readonly IntPtr Bottom = new IntPtr(1);
        private static readonly IntPtr Topmost = new IntPtr(-1);
        private static readonly IntPtr NotTopmost = new IntPtr(-2);
        private readonly NativeWindow marker = new NativeWindow();
        private readonly string markerCaption = "DesktopTodo.DesktopOrder." + Guid.NewGuid().ToString("N");
        private readonly WidgetForm widget;
        private readonly Timer timer = new Timer { Interval = 200 };
        private IntPtr iconsHost;
        private DateTime nextHostCheck;
        private DateTime revealUntil;
        private bool? onDesktop;

        public DesktopLayer(WidgetForm widget)
        {
            this.widget = widget;
            // A hidden top-level marker lets us tell when Explorer raises the desktop.
            marker.CreateHandle(new CreateParams
            {
                Caption = markerCaption,
                Style = unchecked((int)0x88000000),
                ExStyle = 0x80
            });
            SetWindowPos(marker.Handle, Bottom, 0, 0, 0, 0, PlaceFlags);
            timer.Tick += delegate { Refresh(false); };
            timer.Start();
        }

        public void Refresh(bool force)
        {
            if (!widget.Visible || widget.ModalIsOpen) return;
            DateTime now = DateTime.UtcNow;
            if (widget.ActivatedFromTray)
            {
                if (now >= revealUntil && GetForegroundWindow() != widget.Handle) widget.ReturnToDesktop();
                return;
            }
            if (force || now >= nextHostCheck || !IsWindow(iconsHost))
            {
                IntPtr newHost = FindIconsHost();
                if (newHost != iconsHost)
                {
                    iconsHost = newHost;
                    SetWindowPos(marker.Handle, Bottom, 0, 0, 0, 0, PlaceFlags);
                    force = true;
                }
                nextHostCheck = now.AddSeconds(2);
            }

            bool showingDesktop = IsAboveMarker(iconsHost);
            if (!force && onDesktop == showingDesktop) return;
            onDesktop = showingDesktop;
            SetWindowPos(widget.Handle, showingDesktop ? Topmost : Bottom, 0, 0, 0, 0, PlaceFlags);
        }

        public void Reveal()
        {
            revealUntil = DateTime.UtcNow.AddSeconds(5);
            SetWindowPos(widget.Handle, NotTopmost, 0, 0, 0, 0, PlaceFlags);
            SetWindowPos(widget.Handle, IntPtr.Zero, 0, 0, 0, 0, PlaceFlags);
            widget.Activate();
        }

        private bool IsAboveMarker(IntPtr host)
        {
            return host != IntPtr.Zero && IsWindowVisible(host) &&
                   FindWindowEx(IntPtr.Zero, host, null, markerCaption) == marker.Handle;
        }

        private static IntPtr FindIconsHost()
        {
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) return IntPtr.Zero;
            if (HasDesktopIcons(shell)) return shell;
            uint shellProcess;
            GetWindowThreadProcessId(shell, out shellProcess);
            IntPtr host = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr data)
            {
                uint process;
                GetWindowThreadProcessId(window, out process);
                if (process != shellProcess || !IsWindowVisible(window)) return true;
                var className = new StringBuilder(64);
                GetClassName(window, className, className.Capacity);
                if (className.ToString() != "WorkerW" || !HasDesktopIcons(window)) return true;
                host = window;
                return false;
            }, IntPtr.Zero);
            return host;
        }

        private static bool HasDesktopIcons(IntPtr window)
        {
            return FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
        }

        public void Dispose()
        {
            timer.Dispose();
            marker.DestroyHandle();
        }

        private delegate bool EnumCallback(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
