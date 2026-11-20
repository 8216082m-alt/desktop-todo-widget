using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopTodo
{
    internal static class Theme
    {
        public static readonly Color Paper = Color.FromArgb(251, 250, 246);
        public static readonly Color White = Color.FromArgb(255, 255, 253);
        public static readonly Color Ink = Color.FromArgb(29, 45, 43);
        public static readonly Color Muted = Color.FromArgb(84, 103, 99);
        public static readonly Color Line = Color.FromArgb(221, 228, 222);
        public static readonly Color Green = Color.FromArgb(17, 100, 85);
        public static readonly Color Mint = Color.FromArgb(225, 240, 233);
        public static readonly Color Red = Color.FromArgb(156, 56, 46);
        public static readonly Color Rose = Color.FromArgb(251, 237, 231);
        public static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
        private static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();

        public static Font Font(float points, bool bold = false)
        {
            string key = points.ToString(CultureInfo.InvariantCulture) + (bold ? "b" : "");
            Font font;
            if (!Fonts.TryGetValue(key, out font))
            {
                font = new Font("Microsoft YaHei UI", points, bold ? FontStyle.Bold : FontStyle.Regular);
                Fonts.Add(key, font);
            }
            return font;
        }

        public static Label Label(string title, float points, Color color, bool bold = false)
        {
            return new Label
            {
                Text = title,
                ForeColor = color,
                Font = Font(points, bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                UseMnemonic = false,
                Margin = Padding.Empty
            };
        }

        public static Button Button(string title, Color ink, Color fill, float points = 10, bool bold = false)
        {
            var button = new Button
            {
                Text = title, Font = Font(points, bold), ForeColor = ink, BackColor = fill,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
                UseMnemonic = false, Margin = Padding.Empty
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ink == Color.White ? Color.FromArgb(13, 82, 70) :
                                                       fill == Rose ? Color.FromArgb(244, 222, 215) : Mint;
            button.FlatAppearance.MouseDownBackColor = button.FlatAppearance.MouseOverBackColor;
            return button;
        }

        public static string Deadline(DateTime utc)
        {
            DateTime local = utc.ToLocalTime();
            string format = local.Year == DateTime.Now.Year ? "M月d日 HH:mm" : "yyyy年M月d日 HH:mm";
            return local.ToString(format, Chinese);
        }
    }

    internal sealed class WidgetForm : Form
    {
        private readonly WidgetContext context;
        private readonly FlowLayoutPanel list;
        private readonly Button pendingTab;
        private readonly Button completedTab;
        private readonly Label date;
        private readonly Label title;
        private bool canActivate;
        private bool completedView;
        private string renderedGroups;
        public bool ActivatedFromTray { get { return canActivate; } }
        public bool ModalIsOpen { get; set; }

        public WidgetForm(WidgetContext context, Icon icon)
        {
            this.context = context;
            Text = "桌面待办";
            Icon = icon;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Font(10);
            BackColor = Theme.Paper;
            ForeColor = Theme.Ink;
            ClientSize = new Size(386, 526);
            DoubleBuffered = true;
            AccessibleName = Text;

            var header = new Panel { Dock = DockStyle.Top, Size = new Size(ClientSize.Width, 128), BackColor = Theme.Paper };
            var bar = new Panel { Bounds = new Rectangle(0, 0, 306, 30), Cursor = Cursors.SizeAll };
            bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            bar.MouseDown += DragFromHeader;
            header.Controls.Add(bar);

            var settings = Theme.Button("⋯", Theme.Muted, Theme.Paper, 13, true);
            settings.AccessibleName = "设置";
            settings.Click += delegate { context.ShowSettings(); };
            var hide = Theme.Button("−", Theme.Muted, Theme.Paper, 14);
            hide.AccessibleName = "隐藏到托盘";
            hide.Click += delegate { Hide(); };
            header.Controls.Add(settings);
            header.Controls.Add(hide);
            settings.SetBounds(307, 4, 32, 26);
            hide.SetBounds(341, 4, 32, 26);
            settings.Anchor = hide.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            title = Theme.Label("待办", 23, Theme.Ink, true);
            title.SetBounds(20, 26, 210, 42);
            date = Theme.Label("", 10, Theme.Muted);
            date.SetBounds(22, 71, 224, 22);
            header.Controls.Add(title);
            header.Controls.Add(date);

            var add = Theme.Button("＋ 新增", Color.White, Theme.Green, 10, true);
            add.AccessibleName = "新增待办";
            add.SetBounds(269, 47, 96, 37);
            add.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            add.Click += delegate { context.EditTask(null); };
            header.Controls.Add(add);

            list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(20, 2, 4, 8), TabStop = false,
                BackColor = Theme.Paper, AccessibleName = "待办清单"
            };
            list.SizeChanged += delegate { SizeRows(); };

            pendingTab = Theme.Button("待办", Theme.Green, Theme.Mint, 9.5f, true);
            completedTab = Theme.Button("已完成", Theme.Muted, Theme.Paper, 9.5f, true);
            pendingTab.SetBounds(21, 99, 78, 28);
            completedTab.SetBounds(105, 99, 94, 28);
            pendingTab.Click += delegate { SelectView(false); };
            completedTab.Click += delegate { SelectView(true); };
            header.Controls.Add(pendingTab);
            header.Controls.Add(completedTab);

            var rule = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line };
            Controls.Add(list);
            Controls.Add(rule);
            Controls.Add(header);
            Deactivate += delegate
            {
                if (canActivate && !ModalIsOpen) ReturnToDesktop();
            };
            VisibleChanged += delegate
            {
                if (!Visible && canActivate) ReturnToDesktop();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (args.CloseReason != CloseReason.UserClosing || context.Exiting) return;
                args.Cancel = true;
                Hide();
            };
            ResumeLayout(true);
        }

        protected override bool ShowWithoutActivation { get { return !canActivate; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x80;
                if (!canActivate) parameters.ExStyle |= 0x08000000;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x21 && !canActivate)
            {
                message.Result = new IntPtr(3);
                return;
            }
            if (message.Msg == 0x46 && !canActivate)
            {
                int flagsOffset = IntPtr.Size * 2 + sizeof(int) * 4;
                int flags = Marshal.ReadInt32(message.LParam, flagsOffset);
                Marshal.WriteInt32(message.LParam, flagsOffset, flags | 0x0004);
            }
            base.WndProc(ref message);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.N))
            {
                context.EditTask(null);
                return true;
            }
            if (keyData == Keys.Escape)
            {
                ReturnToDesktop();
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        public void OpenFromTray()
        {
            canActivate = true;
            bool wasHidden = !Visible;
            if (wasHidden) Show();
            NativeSetStyle(Handle, NativeGetStyle(Handle) & ~0x08000000);
            context.Reveal();
        }

        public void ReturnToDesktop()
        {
            canActivate = false;
            if (IsHandleCreated) NativeSetStyle(Handle, NativeGetStyle(Handle) | 0x08000000);
            context.RefreshLayer();
        }

        public void PreparePosition(WidgetState state)
        {
            var working = Screen.PrimaryScreen.WorkingArea;
            Point defaultPoint = new Point(working.Right - Width - 26, working.Top + 44);
            Location = ClampToScreen(state.X.HasValue && state.Y.HasValue ?
                                    new Point(state.X.Value, state.Y.Value) : defaultPoint);
        }

        public void OnDisplayChange()
        {
            Location = ClampToScreen(Location);
        }

        private Point ClampToScreen(Point location)
        {
            Rectangle working = Screen.FromRectangle(new Rectangle(location, Size)).WorkingArea;
            return new Point(Math.Max(working.Left, Math.Min(location.X, working.Right - Width)),
                             Math.Max(working.Top, Math.Min(location.Y, working.Bottom - Height)));
        }

        public void SelectView(bool completed)
        {
            completedView = completed;
            Render(context.State, DateTime.UtcNow, true, false);
        }

        public void Render(WidgetState state, DateTime now, bool force, bool preserveScroll = true)
        {
            string day = now.ToLocalTime().ToString("M月d日 dddd", Theme.Chinese);
            date.Text = day;
            string groups = completedView ? "done" : string.Join("", state.Tasks.Where(item => !item.CompletedUtc.HasValue)
                                                                                 .Select(item => ReminderRules.Group(item, now)));
            if (!force && groups == renderedGroups) return;
            renderedGroups = groups;
            int pendingCount = state.Tasks.Count(task => !task.CompletedUtc.HasValue);
            SetTab(pendingTab, "待办", pendingCount, !completedView);
            SetTab(completedTab, "已完成", state.Tasks.Count - pendingCount, completedView);

            int oldScroll = preserveScroll ? -list.AutoScrollPosition.Y : 0;
            list.SuspendLayout();
            try
            {
                list.AutoScrollPosition = Point.Empty;
                foreach (Control control in list.Controls.Cast<Control>().ToArray()) control.Dispose();
                if (completedView)
                {
                    foreach (var task in state.Tasks.Where(item => item.CompletedUtc.HasValue)
                                                    .OrderByDescending(item => item.CompletedUtc))
                        list.Controls.Add(CreateRow(task, false));
                }
                else
                {
                    string[] names = { "逾期", "今天", "之后", "无截止时间" };
                    var taskGroups = state.Tasks.Where(task => !task.CompletedUtc.HasValue)
                                                .GroupBy(task => ReminderRules.Group(task, now))
                                                .OrderBy(group => group.Key);
                    foreach (var group in taskGroups)
                    {
                        var groupHeading = Theme.Label(names[group.Key], 9.5f, group.Key == 0 ? Theme.Red : Theme.Muted, true);
                        groupHeading.Height = 34;
                        list.Controls.Add(groupHeading);
                        foreach (var task in group.OrderBy(task => task.DeadlineUtc).ThenBy(task => task.CreatedUtc))
                            list.Controls.Add(CreateRow(task, group.Key == 0));
                    }
                }
                if (list.Controls.Count == 0) AddEmptyState();
                SizeRows();
            }
            finally
            {
                list.ResumeLayout(true);
            }
            if (oldScroll > 0) list.AutoScrollPosition = new Point(0, oldScroll);
        }

        private static void SetTab(Button tab, string name, int count, bool selected)
        {
            tab.Text = name + "  " + count;
            tab.AccessibleName = name + "，" + count + " 项" + (selected ? "，已选中" : "");
            tab.BackColor = selected ? Theme.Mint : Theme.Paper;
            tab.ForeColor = selected ? Theme.Green : Theme.Muted;
        }

        private void AddEmptyState()
        {
            var empty = new Panel { Height = 160, Margin = Padding.Empty };
            var mark = Theme.Label(completedView ? "✓" : "＋", 22, Theme.Green, true);
            mark.TextAlign = ContentAlignment.MiddleCenter;
            mark.SetBounds(0, 47, TotalRowWidth, 48);
            mark.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            empty.Width = TotalRowWidth;
            var message = Theme.Label(completedView ? "还没有已完成的待办" : "还没有待办", 11, Theme.Muted);
            message.TextAlign = ContentAlignment.MiddleCenter;
            message.SetBounds(0, 99, TotalRowWidth, 29);
            message.Anchor = mark.Anchor;
            empty.Controls.Add(mark);
            empty.Controls.Add(message);
            list.Controls.Add(empty);
        }

        private Control CreateRow(Todo task, bool overdue)
        {
            bool done = task.CompletedUtc.HasValue;
            string note = string.Join(" ", (task.Notes ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            Color fill = overdue ? Theme.Rose : Theme.White;
            var row = new Panel
            {
                Height = note.Length == 0 ? 60 : 81,
                Width = TotalRowWidth,
                BackColor = fill,
                Margin = new Padding(0, 0, 0, 7)
            };
            row.Paint += delegate(object sender, PaintEventArgs args)
            {
                var edge = overdue ? Color.FromArgb(241, 217, 210) : Theme.Line;
                ControlPaint.DrawBorder(args.Graphics, row.ClientRectangle, edge, ButtonBorderStyle.Solid);
            };

            var check = new ToggleMark(done, overdue) { AccessibleName = (done ? "恢复待办 " : "完成待办 ") + task.Title };
            check.SetBounds(7, 9, 33, 33);
            check.Click += delegate { context.ToggleTask(task.Id); };
            row.Controls.Add(check);

            var name = Theme.Label(task.Title, 10.5f, done ? Theme.Muted : Theme.Ink, true);
            name.SetBounds(47, 7, row.Width - 90, 25);
            name.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var time = Theme.Label(task.DeadlineUtc.HasValue ? Theme.Deadline(task.DeadlineUtc.Value) :
                                  (done ? "完成于 " + Theme.Deadline(task.CompletedUtc.Value) : ""),
                                  9, overdue ? Theme.Red : Theme.Muted);
            time.SetBounds(47, 32, row.Width - 90, 20);
            time.Anchor = name.Anchor;

            var open = Theme.Button("›", Theme.Muted, fill, 15);
            open.AccessibleName = "编辑待办 " + task.Title;
            open.SetBounds(row.Width - 35, 9, 28, 33);
            open.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            EventHandler openTask = delegate { context.EditTask(task.Id); };
            open.Click += openTask;
            name.Cursor = time.Cursor = Cursors.Hand;
            name.Click += openTask;
            time.Click += openTask;
            row.Controls.Add(name);
            row.Controls.Add(time);
            row.Controls.Add(open);
            if (note.Length != 0)
            {
                var summary = Theme.Label(note, 9, Theme.Muted);
                summary.SetBounds(47, 52, row.Width - 66, 20);
                summary.Anchor = name.Anchor;
                summary.Cursor = Cursors.Hand;
                summary.Click += openTask;
                row.Controls.Add(summary);
            }
            return row;
        }

        private int TotalRowWidth { get { return Math.Max(100, list.ClientSize.Width - 42); } }

        private void SizeRows()
        {
            foreach (Control control in list.Controls) control.Width = TotalRowWidth;
        }

        private void DragFromHeader(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Point origin = Location;
            Point cursor = Control.MousePosition;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, new IntPtr(2), new IntPtr((cursor.X & 0xffff) | (cursor.Y << 16)));
            Location = ClampToScreen(Location);
            if (Location != origin) context.SavePosition(Location);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong(IntPtr window, int index, int value);
        private static int NativeGetStyle(IntPtr window) { return GetWindowLong(window, -20); }
        private static void NativeSetStyle(IntPtr window, int style) { SetWindowLong(window, -20, style); }
    }

    internal sealed class ToggleMark : Button
    {
        private readonly bool done;
        private readonly bool overdue;

        public ToggleMark(bool done, bool overdue)
        {
            this.done = done;
            this.overdue = overdue;
            BackColor = overdue ? Theme.Rose : Theme.White;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float unit = Math.Min(Width, Height) / 33f;
            var square = new RectangleF(8 * unit, 8 * unit, 17 * unit, 17 * unit);
            using (var pen = new Pen(done ? Color.White : overdue ? Theme.Red : Theme.Muted, 1.35f * unit))
            using (var brush = new SolidBrush(Theme.Green))
            {
                if (done)
                {
                    e.Graphics.FillRectangle(brush, square);
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new PointF(12 * unit, 16 * unit), new PointF(15.5f * unit, 20 * unit),
                        new PointF(22 * unit, 13 * unit)
                    });
                }
                else e.Graphics.DrawRectangle(pen, square.X, square.Y, square.Width, square.Height);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
        }
    }
}
