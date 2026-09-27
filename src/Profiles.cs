using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MintClicker
{
    public sealed class ClickProfile
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "默认方案";
        public Preferences Settings = new Preferences();
        public override string ToString() { return Name; }
    }

    public sealed class ProfileLibrary
    {
        public int Version = 2;
        public string SelectedId;
        public List<ClickProfile> Profiles = new List<ClickProfile>();
    }

    public sealed class ProfileExport
    {
        public int schemaVersion = 2;
        public string name;
        public Preferences settings;
    }

    internal static class ProfileStore
    {
        internal const int MaxPoints = 1000;
        internal const int MaxProfiles = 100;
        private const int MaxBytes = 32 * 1024 * 1024;
        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 32 };
        }
        internal static T Copy<T>(T value)
        {
            JavaScriptSerializer json = Serializer();
            return json.Deserialize<T>(json.Serialize(value));
        }
        internal static string ValidateName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Trim().Length > 60)
                throw new InvalidDataException("方案名称需要 1～60 个字符。");
            foreach (char value in name) if (Char.IsControl(value)) throw new InvalidDataException("方案名称不能包含控制字符。");
            return name.Trim();
        }
        internal static void ValidateSettings(Preferences prefs)
        {
            if (prefs == null) throw new InvalidDataException("方案缺少 settings 设置。");
            if (prefs.Interval < 10 || prefs.Interval > 3600000 || prefs.Button < 0 || prefs.Button > 2 ||
                prefs.Mode < 0 || prefs.Mode > 1 || prefs.Limit < 0 || prefs.Limit > 1000000000 ||
                prefs.PositionMode < -1 || prefs.PositionMode > 3)
                throw new InvalidDataException("方案中的间隔、按键、模式或循环次数超出范围。");
            ValidateCoordinate(prefs.X, prefs.Y);
            if (Array.IndexOf(new[] { 0, 1, 3, 5, 10 }, prefs.StartupDelay) < 0 || prefs.TargetTitle == null || prefs.TargetProcess == null ||
                prefs.TargetTitle.Length > 1024 || prefs.TargetProcess.Length > 260)
                throw new InvalidDataException("启动延时或目标窗口设置无效。");
            if (prefs.Actions == null || prefs.Actions.Count > MaxPoints) throw new InvalidDataException("宏步骤最多 1000 项。");
            foreach (MacroEntry entry in prefs.Actions) ActionCompiler.Validate(entry);
            if (prefs.LoopStart < 0 || prefs.LoopEnd < 0 || prefs.LoopStart > MaxPoints || prefs.LoopEnd > MaxPoints || prefs.LoopCount < 1 || prefs.LoopCount > 10000)
                throw new InvalidDataException("局部循环设置无效。");
            if (prefs.Positions == null || prefs.Positions.Count > MaxPoints)
                throw new InvalidDataException("每个方案最多保存 1000 个点位。");
            foreach (PositionEntry point in prefs.Positions)
            {
                if (point == null) throw new InvalidDataException("方案包含空点位。");
                ValidateCoordinate(point.X, point.Y);
                if (point.Button == -1) point.Button = prefs.Button;
                if (point.Button < 0 || point.Button > 2 || point.DelayBefore < 0 || point.DelayBefore > 3600000 ||
                    point.DelayAfter < -1 || point.DelayAfter > 3600000 || point.Count < 1 || point.Count > 10000 || point.RepeatInterval < 0 || point.RepeatInterval > 3600000)
                    throw new InvalidDataException("点位按键或延时超出范围。前延时为 0～3600000；后延时 -1 表示沿用全局间隔。");
            }
        }
        private static void ValidateCoordinate(int x, int y)
        {
            if (x < -1000000 || x > 1000000 || y < -1000000 || y > 1000000)
                throw new InvalidDataException("点位坐标超出支持范围。");
        }
        private static T Read<T>(string path)
        {
            using (FileStream file = File.OpenRead(path))
            {
                if (file.Length > MaxBytes) throw new InvalidDataException("方案文件过大（最多 32 MB）。");
                using (StreamReader reader = new StreamReader(file, Encoding.UTF8, true))
                    return Serializer().Deserialize<T>(reader.ReadToEnd());
            }
        }
        private static void Write<T>(string path, T value, bool backup)
        {
            string json = Serializer().Serialize(value);
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("方案数据超过 32 MB，请减少方案或点位。");
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".mint-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, backup ? path + ".bak" : null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static ProfileLibrary Load(string path)
        {
            ProfileLibrary library = Read<ProfileLibrary>(path);
            ValidateLibrary(library);
            return library;
        }
        private static void ValidateLibrary(ProfileLibrary library)
        {
            if (library == null || (library.Version != 1 && library.Version != 2) || library.Profiles == null || library.Profiles.Count == 0 || library.Profiles.Count > MaxProfiles)
                throw new InvalidDataException("方案库版本或方案数量无效。");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ClickProfile profile in library.Profiles)
            {
                if (profile == null || String.IsNullOrWhiteSpace(profile.Id) || !ids.Add(profile.Id))
                    throw new InvalidDataException("方案标识重复或为空。");
                profile.Name = ValidateName(profile.Name);
                if (!names.Add(profile.Name)) throw new InvalidDataException("方案名称重复。");
                ValidateSettings(profile.Settings);
            }
            if (!ids.Contains(library.SelectedId)) throw new InvalidDataException("上次选中的方案不存在。");
        }
        internal static void Save(string path, ProfileLibrary library)
        {
            ValidateLibrary(library);
            library.Version = 2;
            Write(path, library, true);
        }
        internal static ClickProfile Import(string path)
        {
            ProfileExport data = Read<ProfileExport>(path);
            if (data == null || (data.schemaVersion != 1 && data.schemaVersion != 2)) throw new InvalidDataException("不支持此方案文件版本。");
            string name = ValidateName(data.name);
            ValidateSettings(data.settings);
            return new ClickProfile { Name = name, Settings = data.settings };
        }
        internal static void Export(string path, string name, Preferences settings)
        {
            ValidateSettings(settings);
            Write(path, new ProfileExport { name = ValidateName(name), settings = settings }, false);
        }
    }
}
