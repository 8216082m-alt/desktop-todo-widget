using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DesktopTodo
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                "DesktopTodoWidget");
            string startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string startupArgs = "--startup";
            string suffix;
            using (var sha = SHA256.Create())
                suffix = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))
                                .Replace('/', '_');

            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DesktopTodo.Open." + suffix))
            using (var mutex = new Mutex(false, @"Local\DesktopTodo.Instance." + suffix))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    if (!args.Contains("--startup")) signal.Set();
                    return;
                }
                try
                {
                    var startup = new LoginShortcut(startupDirectory, Application.ExecutablePath, startupArgs);
                    using (var context = new WidgetContext(new StateStore(dataDirectory), startup, signal))
                        Application.Run(context);
                }
                catch (Exception e)
                {
                    MessageBox.Show(e.Message, "桌面待办", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }

    internal sealed class WidgetContext : ApplicationContext
    {
        private readonly StateStore store;
        private readonly LoginShortcut startup;
        private readonly Icon icon;
        private readonly WidgetForm widget;
        private readonly DesktopLayer desktop;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly System.Windows.Forms.Timer reminderTimer;
        private readonly RegisteredWaitHandle revealSignal;
        private List<Reminder> lastNotification = new List<Reminder>();
        private bool notificationPending;
        private bool storageErrorReported;
        public WidgetState State { get; private set; }
        public bool Exiting { get; private set; }
        public bool StartupDesired { get; private set; }
        public string StartupError { get; private set; }

        public WidgetContext(StateStore store, LoginShortcut startup, EventWaitHandle signal)
        {
            this.store = store;
            this.startup = startup;
            State = store.Load();
            StartupDesired = !State.StartupConfigured || startup.Enabled;
            bool recovered = store.LoadedBackup;
            icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone();
            widget = new WidgetForm(this, icon);
            IntPtr handle = widget.Handle;

            desktop = new DesktopLayer(widget);
            widget.PreparePosition(State);
            widget.Render(State, DateTime.UtcNow, true);
            widget.Show();
            desktop.Refresh(true);

            menu = new ContextMenuStrip();
            menu.Items.Add("打开待办", null, delegate { OpenFromTray(); });
            menu.Items.Add("新增待办", null, delegate { EditTask(null); });
            menu.Items.Add("隐藏挂件", null, delegate { widget.Hide(); });
            menu.Items.Add("设置", null, delegate { ShowSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitThread(); });
            tray = new NotifyIcon { Icon = icon, Text = "桌面待办", ContextMenuStrip = menu, Visible = true };
            tray.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) OpenFromTray();
            };
            tray.BalloonTipClicked += delegate { OpenNotification(); };

            reminderTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            reminderTimer.Tick += delegate { CheckReminders(); };
            reminderTimer.Start();
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            revealSignal = ThreadPool.RegisterWaitForSingleObject(signal,
                delegate { Dispatch(OpenFromTray); }, null, Timeout.Infinite, false);
            widget.BeginInvoke((Action)delegate
            {
                if (StartupDesired) SetStartup(true);
                if (recovered)
                    tray.ShowBalloonTip(5000, "桌面待办", "已从上一份有效备份恢复待办。", ToolTipIcon.Info);
                CheckReminders();
            });
        }

        private void Dispatch(Action action)
        {
            if (Exiting) return;
            try
            {
                widget.BeginInvoke((Action)delegate { if (!Exiting) action(); });
            }
            catch (InvalidOperationException) { }
        }

        private void DisplayChanged(object sender, EventArgs args)
        {
            Dispatch(delegate { widget.OnDisplayChange(); desktop.Refresh(true); });
        }

        private void PowerChanged(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode == PowerModes.Resume) Dispatch(CheckReminders);
        }

        private void SessionChanged(object sender, SessionSwitchEventArgs args)
        {
            if (args.Reason == SessionSwitchReason.SessionUnlock) Dispatch(CheckReminders);
        }

        public void Reveal() { desktop.Reveal(); }
        public void RefreshLayer() { desktop.Refresh(true); }

        private void OpenFromTray()
        {
            if (widget.ModalIsOpen) return;
            widget.OpenFromTray();
        }

        public void EditTask(string id)
        {
            if (widget.ModalIsOpen) return;
            var existing = State.Tasks.FirstOrDefault(task => task.Id == id);
            if (id != null && existing == null) { OpenFromTray(); return; }
            using (var dialog = new TaskEditor(existing, SaveTask, delegate { return DeleteTask(id); }))
                ShowDialog(dialog);
            CheckReminders();
        }

        private DialogResult ShowDialog(Form dialog)
        {
            if (!widget.Visible) widget.Show();
            widget.ModalIsOpen = true;
            dialog.Icon = icon;
            try { return dialog.ShowDialog(widget); }
            finally
            {
                widget.ModalIsOpen = false;
                if (widget.ActivatedFromTray && !widget.ContainsFocus) widget.ReturnToDesktop();
                desktop.Refresh(true);
                if (notificationPending)
                {
                    notificationPending = false;
                    Dispatch(OpenNotification);
                }
            }
        }

        public void ShowSettings()
        {
            if (widget.ModalIsOpen) return;
            using (var settings = new SettingsDialog(this)) ShowDialog(settings);
        }

        public void SetStartup(bool enabled)
        {
            StartupDesired = enabled;
            try
            {
                startup.SetEnabled(enabled);
                if (!State.StartupConfigured)
                {
                    var next = State.Copy();
                    next.StartupConfigured = true;
                    store.Save(next);
                    State = next;
                }
                StartupError = null;
            }
            catch (Exception e)
            {
                StartupError = "无法更新登录启动项。请重试。";
                System.Diagnostics.Debug.WriteLine(e);
            }
        }

        private bool SaveTask(Todo changed)
        {
            var next = State.Copy();
            int index = next.Tasks.FindIndex(task => task.Id == changed.Id);
            if (index < 0) next.Tasks.Add(changed);
            else
            {
                var current = next.Tasks[index];
                bool sameDeadline = current.DeadlineUtc == changed.DeadlineUtc;
                changed.DueHandled = sameDeadline && current.DueHandled;
                changed.AdvanceHandled = sameDeadline && current.AdvanceMinutes == changed.AdvanceMinutes &&
                                         current.AdvanceHandled;
                next.Tasks[index] = changed;
            }
            if (!Commit(next, true)) return false;
            if (index < 0) widget.SelectView(false);
            else widget.Render(State, DateTime.UtcNow, true);
            return true;
        }

        public void ToggleTask(string id)
        {
            var next = State.Copy();
            var task = next.Tasks.FirstOrDefault(item => item.Id == id);
            if (task == null) return;
            DateTime now = DateTime.UtcNow;
            if (task.CompletedUtc.HasValue) ReminderRules.Restore(task, now);
            else task.CompletedUtc = now;
            if (Commit(next, true)) widget.Render(State, now, true);
        }

        private bool DeleteTask(string id)
        {
            var next = State.Copy();
            next.Tasks.RemoveAll(task => task.Id == id);
            if (!Commit(next, true)) return false;
            widget.Render(State, DateTime.UtcNow, true);
            return true;
        }

        public void SavePosition(Point point)
        {
            var next = State.Copy();
            next.X = point.X;
            next.Y = point.Y;
            Commit(next, true);
        }

        private bool Commit(WidgetState next, bool interactive)
        {
            try
            {
                store.Save(next);
                State = next;
                storageErrorReported = false;
                return true;
            }
            catch (Exception e)
            {
                if (interactive)
                    MessageBox.Show(Form.ActiveForm ?? widget, "无法保存待办数据。请确认本机磁盘可写后重试。\n" + e.Message,
                                    "桌面待办", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else if (!storageErrorReported)
                {
                    storageErrorReported = true;
                    tray.ShowBalloonTip(6000, "桌面待办", "暂时无法保存提醒，稍后会重试。", ToolTipIcon.Warning);
                }
                return false;
            }
        }

        private void CheckReminders()
        {
            if (Exiting) return;
            DateTime now = DateTime.UtcNow;
            widget.Render(State, now, false);
            var next = State.Copy();
            bool changed;
            var due = ReminderRules.Collect(next, now, out changed);
            if (!changed || !Commit(next, false) || due.Count == 0) return;

            lastNotification = due;
            if (due.Count == 1)
            {
                var reminder = due[0];
                string title = reminder.IsDue ? "待办已到期" : "待办即将到期";
                string body = Condense(reminder.Task.Title, 105) + "\n截止 " + Theme.Deadline(reminder.Task.DeadlineUtc.Value);
                tray.ShowBalloonTip(7000, title, body, ToolTipIcon.Info);
            }
            else
            {
                string body = string.Join("\n", due.Take(3).Select(reminder =>
                    (reminder.IsDue ? "到期  " : "提前  ") + Condense(reminder.Task.Title, 62)));
                tray.ShowBalloonTip(7000, due.Count + " 条待办提醒", body, ToolTipIcon.Info);
            }
        }

        private static string Condense(string value, int max)
        {
            if (value.Length <= max) return value;
            return value.Substring(0, char.IsHighSurrogate(value[max - 1]) ? max - 1 : max) + "…";
        }

        private void OpenNotification()
        {
            if (widget.ModalIsOpen) { notificationPending = true; return; }
            if (lastNotification.Count == 0) { OpenFromTray(); return; }
            if (lastNotification.Count == 1)
            {
                EditTask(lastNotification[0].Task.Id);
                return;
            }
            using (var dialog = new ReminderDialog(lastNotification))
                if (ShowDialog(dialog) == DialogResult.OK) EditTask(dialog.SelectedId);
        }

        protected override void ExitThreadCore()
        {
            Exiting = true;
            tray.Visible = false;
            widget.Close();
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Exiting = true;
                revealSignal.Unregister(null);
                SystemEvents.DisplaySettingsChanged -= DisplayChanged;
                SystemEvents.PowerModeChanged -= PowerChanged;
                SystemEvents.SessionSwitch -= SessionChanged;
                reminderTimer.Dispose();
                tray.Dispose();
                menu.Dispose();
                desktop.Dispose();
                widget.Dispose();
                icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
