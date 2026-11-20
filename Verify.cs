using System;
using System.IO;
using System.Linq;
using DesktopTodo;

internal static class Verify
{
    [STAThread]
    private static void Main(string[] args)
    {
        string testRoot = Path.Combine(args[0], Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var store = new StateStore(testRoot);
        var state = store.Load();
        DateTime deadline = DateTime.UtcNow.Date.AddHours(12);
        var active = new Todo { Title = "提交周报", Notes = "更新进度\r\n核对附件", DeadlineUtc = deadline };
        var completed = new Todo { Title = "已完成", DeadlineUtc = deadline, CompletedUtc = deadline.AddHours(-2) };
        var quiet = new Todo { Title = "仅提前提醒", DeadlineUtc = deadline, RemindAtDue = false, AdvanceMinutes = 15 };
        var noDeadline = new Todo { Title = "无截止" };
        state.Tasks.AddRange(new[] { active, completed, quiet, noDeadline });
        bool changed;
        Assert(ReminderRules.Collect(state, deadline.AddMinutes(-61), out changed).Count == 0 && !changed, "before threshold");
        var advance = ReminderRules.Collect(state, deadline.AddMinutes(-60), out changed);
        Assert(advance.Count == 1 && changed && !advance[0].IsDue && advance[0].Task.Id == active.Id, "default advance");
        store.Save(state);
        state = store.Load();
        Assert(state.Tasks[0].Notes == "更新进度\r\n核对附件", "Chinese multiline persistence");
        Assert(state.Tasks[0].DeadlineUtc.Value.Kind == DateTimeKind.Utc, "UTC persistence");
        Assert(ReminderRules.Collect(state, deadline.AddMinutes(-59), out changed).Count == 0 && !changed, "restart deduplication");
        var due = ReminderRules.Collect(state, deadline.AddHours(4), out changed);
        Assert(due.Count == 1 && changed && due[0].IsDue && due[0].Task.Id == active.Id, "catch-up, complete and disabled");
        Assert(ReminderRules.Collect(state, deadline.AddHours(5), out changed).Count == 0 && !changed, "due deduplication");
        ReminderRules.Restore(state.Tasks[1], deadline.AddMinutes(10));
        Assert(ReminderRules.Collect(state, deadline.AddHours(5), out changed).Count == 0, "do not replay completed period");
        var batch = new WidgetState();
        batch.Tasks.AddRange(new[] { new Todo { Title = "A", DeadlineUtc = deadline }, new Todo { Title = "B", DeadlineUtc = deadline } });
        Assert(ReminderRules.Collect(batch, deadline, out changed).Count == 2, "same-tick aggregation");
        Assert(batch.Tasks.All(item => item.AdvanceHandled), "suppress expired advance reminder");
        Assert(ReminderRules.Group(noDeadline, deadline) == 3 && ReminderRules.Group(active, deadline) == 0, "deadline grouping");
        state.X = 738;
        state.Y = 70;
        store.Save(state);
        Assert(store.Load().X == 738 && store.Load().Y == 70, "position persistence");
        File.WriteAllText(Path.Combine(testRoot, "state.json"), "invalid");
        var recovered = store.Load();
        Assert(store.LoadedBackup && recovered.Tasks.Count == 4, "read previous backup after corruption");
        store.Save(recovered);
        Assert(store.Load().Tasks.Count == 4, "recover main file");
        string shortcutDirectory = Path.Combine(testRoot, "startup");
        var shortcut = new LoginShortcut(shortcutDirectory, args[1], "--startup");
        shortcut.SetEnabled(true);
        Assert(shortcut.Enabled, "enable current-user startup in isolated directory");
        shortcut.SetEnabled(false);
        Assert(!shortcut.Enabled, "disable startup");
        Console.WriteLine("PASS: timing, catch-up, deduplication, grouping, Chinese notes, UTC, position, backup recovery and startup switch");
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
