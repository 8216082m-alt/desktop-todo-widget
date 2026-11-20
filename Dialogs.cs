using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopTodo
{
    internal abstract class WidgetDialog : Form
    {
        protected WidgetDialog(string title, Size size)
        {
            SuspendLayout();
            Text = title;
            BackColor = Theme.Paper;
            ForeColor = Theme.Ink;
            Font = Theme.Font(10);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = size;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
        }

        protected T Place<T>(T item, int x, int y, int w, int h) where T : Control
        {
            item.SetBounds(x, y, w, h);
            Controls.Add(item);
            return item;
        }

        protected Label Heading(string text, int y)
        {
            return Place(Theme.Label(text, 17, Theme.Ink, true), 22, y, 356, 35);
        }

        protected Button CancelAt(string text, int x, int y)
        {
            var button = Place(Theme.Button(text, Theme.Ink, Theme.Mint, 10), x, y, 76, 34);
            button.DialogResult = DialogResult.Cancel;
            CancelButton = button;
            return button;
        }
    }

    internal sealed class TaskEditor : WidgetDialog
    {
        private readonly Todo original;
        private readonly Func<Todo, bool> trySave;
        private readonly TextBox title;
        private readonly TextBox notes;
        private readonly CheckBox hasDeadline;
        private readonly DateTimePicker date;
        private readonly DateTimePicker time;
        private readonly CheckBox early;
        private readonly NumericUpDown lead;
        private readonly ComboBox units;
        private readonly CheckBox atDue;
        private readonly Label error;

        public TaskEditor(Todo task, Func<Todo, bool> save, Func<bool> delete)
            : base(task == null ? "新增待办" : "编辑待办", new Size(400, 454))
        {
            original = task;
            trySave = save;
            Heading(Text, 12);
            Place(Theme.Label("标题", 10, Theme.Ink, true), 22, 56, 250, 20);
            title = Place(new TextBox
            {
                Text = task == null ? "" : task.Title,
                MaxLength = 240, AccessibleName = "标题", BackColor = Theme.White
            }, 22, 80, 356, 27);
            Place(Theme.Label("备注", 10, Theme.Ink, true), 22, 121, 250, 20);
            notes = Place(new TextBox
            {
                Text = task == null ? "" : task.Notes, Multiline = true, ScrollBars = ScrollBars.Vertical,
                MaxLength = 32000, AccessibleName = "备注", BackColor = Theme.White
            }, 22, 145, 356, 79);

            DateTime preset = task != null && task.DeadlineUtc.HasValue ? task.DeadlineUtc.Value.ToLocalTime() :
                              DateTime.Today.AddHours(DateTime.Now.Hour + 1);
            hasDeadline = Place(new CheckBox
            {
                Text = "设置截止时间", Checked = task != null && task.DeadlineUtc.HasValue, AutoSize = false
            }, 22, 237, 356, 25);
            date = Place(new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Value = preset,
                AccessibleName = "截止日期"
            }, 22, 264, 207, 27);
            time = Place(new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", Value = preset,
                ShowUpDown = true, AccessibleName = "截止时间"
            }, 241, 264, 137, 27);

            early = Place(new CheckBox { Text = "提前提醒", Checked = task == null || task.AdvanceMinutes > 0 },
                          22, 306, 115, 26);
            lead = Place(new NumericUpDown { Minimum = 1, Maximum = 9999, AccessibleName = "提前量" },
                         145, 305, 92, 27);
            units = Place(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "提前量单位" },
                          248, 305, 130, 27);
            units.Items.AddRange(new object[] { "分钟", "小时", "天" });
            int minutes = task == null || task.AdvanceMinutes == 0 ? 60 : task.AdvanceMinutes;
            int unit = minutes % 1440 == 0 ? 2 : minutes % 60 == 0 ? 1 : 0;
            units.SelectedIndex = unit;
            lead.Value = Math.Min(lead.Maximum, minutes / UnitMinutes);
            atDue = Place(new CheckBox { Text = "到期时提醒", Checked = task == null || task.RemindAtDue },
                          22, 338, 356, 25);
            hasDeadline.CheckedChanged += delegate { UpdateReminderControls(); };
            early.CheckedChanged += delegate { UpdateReminderControls(); };
            UpdateReminderControls();

            error = Place(Theme.Label("", 9, Theme.Red), 22, 370, 356, 25);
            error.AccessibleName = "输入提示";
            if (task != null)
            {
                var remove = Place(Theme.Button("删除", Theme.Red, Theme.Rose), 22, 402, 70, 34);
                remove.Click += delegate
                {
                    if (MessageBox.Show(this, "删除「" + original.Title + "」？", "删除待办",
                                        MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                                        MessageBoxDefaultButton.Button2) == DialogResult.Yes && delete())
                        DialogResult = DialogResult.OK;
                };
            }
            CancelAt("取消", 207, 402);
            var submit = Place(Theme.Button("保存", Color.White, Theme.Green, 10, true), 293, 402, 85, 34);
            submit.Click += delegate { Submit(); };
            AcceptButton = submit;
            Shown += delegate { title.Focus(); title.SelectAll(); };
            ResumeLayout(true);
        }

        private int UnitMinutes { get { return new[] { 1, 60, 1440 }[units.SelectedIndex]; } }

        private void UpdateReminderControls()
        {
            date.Enabled = time.Enabled = early.Enabled = atDue.Enabled = hasDeadline.Checked;
            lead.Enabled = units.Enabled = hasDeadline.Checked && early.Checked;
        }

        private void Submit()
        {
            string heading = title.Text.Trim();
            if (heading.Length == 0)
            {
                error.Text = "请填写待办标题。";
                title.Focus();
                return;
            }

            DateTime? deadline = null;
            if (hasDeadline.Checked)
            {
                DateTime local = DateTime.SpecifyKind(date.Value.Date.AddHours(time.Value.Hour)
                                                            .AddMinutes(time.Value.Minute), DateTimeKind.Unspecified);
                if (TimeZoneInfo.Local.IsInvalidTime(local))
                {
                    error.Text = "这个时间因夏令时调整而不存在，请另选时间。";
                    time.Focus();
                    return;
                }
                deadline = TimeZoneInfo.ConvertTimeToUtc(local);
                if (original != null && original.DeadlineUtc.HasValue &&
                    DateTime.SpecifyKind(original.DeadlineUtc.Value.ToLocalTime(), DateTimeKind.Unspecified) == local)
                    deadline = original.DeadlineUtc;
            }

            var task = original == null ? new Todo() : original.Copy();
            int advance = early.Checked ? (int)lead.Value * UnitMinutes : 0;
            task.Title = heading;
            task.Notes = notes.Text.Trim();
            task.DeadlineUtc = deadline;
            task.AdvanceMinutes = advance;
            task.RemindAtDue = atDue.Checked;
            if (trySave(task)) DialogResult = DialogResult.OK;
        }
    }

    internal sealed class SettingsDialog : WidgetDialog
    {
        public SettingsDialog(WidgetContext context) : base("待办设置", new Size(400, 196))
        {
            Heading("设置", 13);
            var startup = Place(new CheckBox { Text = "登录 Windows 后自动启动", Checked = context.StartupDesired },
                                22, 62, 356, 28);
            var status = Place(Theme.Label("", 9, Theme.Muted), 22, 97, 356, 34);
            status.AutoEllipsis = false;
            var retry = Place(Theme.Button("重试", Theme.Green, Theme.Mint), 22, 144, 72, 34);

            Action refresh = delegate
            {
                bool failed = context.StartupError != null;
                retry.Visible = failed;
                status.ForeColor = failed ? Theme.Red : Theme.Muted;
                status.Text = context.StartupError ?? (startup.Checked ? "登录后自动显示挂件并开始提醒。" :
                                                                 "打开桌面待办后开始提醒。");
            };
            startup.CheckedChanged += delegate
            {
                context.SetStartup(startup.Checked);
                refresh();
            };
            retry.Click += delegate
            {
                context.SetStartup(startup.Checked);
                refresh();
            };
            CancelAt("关闭", 302, 144);
            refresh();
            ResumeLayout(true);
        }
    }

    internal sealed class ReminderDialog : WidgetDialog
    {
        public string SelectedId { get; private set; }

        public ReminderDialog(IList<Reminder> reminders) : base("待办提醒", new Size(400, 320))
        {
            Heading("待办提醒", 13);
            var items = Place(new ListBox
            {
                IntegralHeight = false, BackColor = Theme.White, HorizontalScrollbar = true,
                AccessibleName = "触发的提醒"
            }, 22, 63, 356, 181);
            foreach (var reminder in reminders)
                items.Items.Add((reminder.IsDue ? "到期　" : "提前　") + reminder.Task.Title);
            Action open = delegate
            {
                if (items.SelectedIndex < 0) return;
                SelectedId = reminders[items.SelectedIndex].Task.Id;
                DialogResult = DialogResult.OK;
            };
            if (items.Items.Count > 0) items.SelectedIndex = 0;
            items.DoubleClick += delegate { open(); };
            CancelAt("关闭", 218, 265);
            var view = Place(Theme.Button("查看", Color.White, Theme.Green, 10, true), 302, 265, 76, 34);
            view.Click += delegate { open(); };
            AcceptButton = view;
            ResumeLayout(true);
        }
    }
}
