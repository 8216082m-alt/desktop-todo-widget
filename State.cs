using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace DesktopTodo
{
    [DataContract]
    internal sealed class Todo
    {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Title = "";
        [DataMember] public string Notes = "";
        [DataMember] public DateTime CreatedUtc = DateTime.UtcNow;
        [DataMember] public DateTime? DeadlineUtc;
        [DataMember] public DateTime? CompletedUtc;
        [DataMember] public int AdvanceMinutes = 60;
        [DataMember] public bool RemindAtDue = true;
        [DataMember] public bool AdvanceHandled;
        [DataMember] public bool DueHandled;

        public Todo Copy() { return (Todo)MemberwiseClone(); }
    }

    [DataContract]
    internal sealed class WidgetState
    {
        [DataMember] public List<Todo> Tasks = new List<Todo>();
        [DataMember] public int? X;
        [DataMember] public int? Y;
        [DataMember] public bool StartupConfigured;

        public WidgetState Copy()
        {
            var copy = (WidgetState)MemberwiseClone();
            copy.Tasks = Tasks.Select(task => task.Copy()).ToList();
            return copy;
        }
    }

    internal sealed class Reminder
    {
        public readonly Todo Task;
        public readonly bool IsDue;

        public Reminder(Todo task, bool isDue)
        {
            Task = task;
            IsDue = isDue;
        }
    }

    internal static class ReminderRules
    {
        public static List<Reminder> Collect(WidgetState state, DateTime now, out bool changed)
        {
            changed = false;
            var reminders = new List<Reminder>();
            foreach (var task in state.Tasks.Where(item => item.DeadlineUtc.HasValue && !item.CompletedUtc.HasValue)
                                            .OrderBy(item => item.DeadlineUtc))
            {
                var deadline = task.DeadlineUtc.Value;
                if (now >= deadline)
                {
                    if (task.AdvanceMinutes > 0 && !task.AdvanceHandled)
                    {
                        task.AdvanceHandled = true;
                        changed = true;
                    }
                    if (task.RemindAtDue && !task.DueHandled)
                    {
                        task.DueHandled = true;
                        changed = true;
                        reminders.Add(new Reminder(task, true));
                    }
                }
                else if (task.AdvanceMinutes > 0 && !task.AdvanceHandled &&
                         deadline - now <= TimeSpan.FromMinutes(task.AdvanceMinutes))
                {
                    task.AdvanceHandled = true;
                    changed = true;
                    reminders.Add(new Reminder(task, false));
                }
            }
            return reminders;
        }

        public static void Restore(Todo task, DateTime now)
        {
            task.CompletedUtc = null;
            if (!task.DeadlineUtc.HasValue) return;
            if (task.DeadlineUtc.Value <= now) task.DueHandled = true;
            if (task.DeadlineUtc.Value - now <= TimeSpan.FromMinutes(task.AdvanceMinutes))
                task.AdvanceHandled = true;
        }

        public static int Group(Todo task, DateTime now)
        {
            if (!task.DeadlineUtc.HasValue) return 3;
            if (task.DeadlineUtc.Value <= now) return 0;
            return task.DeadlineUtc.Value.ToLocalTime().Date == now.ToLocalTime().Date ? 1 : 2;
        }
    }

    internal sealed class StateStore
    {
        private readonly string path;
        private readonly DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(WidgetState));
        private bool loadedBackup;
        public bool LoadedBackup { get { return loadedBackup; } }

        public StateStore(string directory)
        {
            path = Path.Combine(directory, "state.json");
        }

        public WidgetState Load()
        {
            string backup = path + ".bak";
            if (!File.Exists(path) && !File.Exists(backup)) return new WidgetState();
            var state = Read(path);
            if (state != null) return state;
            state = Read(backup);
            if (state == null)
                throw new IOException("待办数据和备份均无法读取。原文件已保留。\n" + Path.GetDirectoryName(path));
            loadedBackup = true;
            return state;
        }

        private WidgetState Read(string file)
        {
            try
            {
                using (var stream = File.OpenRead(file))
                {
                    var state = (WidgetState)serializer.ReadObject(stream);
                    if (state == null || state.Tasks == null ||
                        state.Tasks.Any(task => task == null || string.IsNullOrWhiteSpace(task.Title) ||
                                                string.IsNullOrWhiteSpace(task.Id) || task.AdvanceMinutes < 0) ||
                        state.Tasks.Select(task => task.Id).Distinct().Count() != state.Tasks.Count)
                        return null;
                    return state;
                }
            }
            catch (IOException) { return null; }
            catch (SerializationException) { return null; }
            catch (FormatException) { return null; }
            catch (System.Xml.XmlException) { return null; }
        }

        public void Save(WidgetState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, state);
                stream.Flush(true);
            }
            if (File.Exists(path))
                File.Replace(temp, path, loadedBackup ? null : path + ".bak");
            else
                File.Move(temp, path);
            loadedBackup = false;
        }
    }

    internal sealed class LoginShortcut
    {
        private readonly string directory;
        private readonly string executable;
        private readonly string arguments;
        private string PathName { get { return Path.Combine(directory, "桌面待办.lnk"); } }
        public bool Enabled { get { return !string.IsNullOrEmpty(directory) && File.Exists(PathName); } }

        public LoginShortcut(string directory, string executable, string arguments)
        {
            this.directory = directory;
            this.executable = executable;
            this.arguments = arguments;
        }

        public void SetEnabled(bool enabled)
        {
            if (string.IsNullOrEmpty(directory)) throw new IOException("找不到 Windows 启动文件夹。");
            if (!enabled)
            {
                File.Delete(PathName);
                return;
            }
            Directory.CreateDirectory(directory);
            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic link = ((dynamic)shell).CreateShortcut(PathName);
                shortcut = link;
                link.TargetPath = executable;
                link.WorkingDirectory = Path.GetDirectoryName(executable);
                link.Arguments = arguments;
                link.Description = "桌面待办";
                link.Save();
            }
            finally
            {
                if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null) Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
