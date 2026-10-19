using System.Reflection;

// The single place the version is written. Assembly metadata, the window title and
// the tray text all read from AppInfo, so a release only has to change one line here.
[assembly: AssemblyTitle("轻点 · 连点器")]
[assembly: AssemblyProduct("轻点连点器")]
[assembly: AssemblyVersion(MintClicker.AppInfo.Version)]
[assembly: AssemblyFileVersion(MintClicker.AppInfo.Version)]

namespace MintClicker
{
    internal static class AppInfo
    {
        internal const string Version = "2.0.11";
        internal const string ShortName = "轻点";
        internal const string Title = "轻点 · 自动点击与宏";
        internal static string WindowTitle { get { return Title + " v" + Version; } }
        internal static string TrayTitle { get { return ShortName + " v" + Version; } }
    }
}
