using System;
using System.IO;
using System.Windows.Forms;

namespace MintClicker
{
    // Portable mode: a file named portable.txt next to the exe keeps the profile
    // library and logs in "data\" beside the program instead of %LOCALAPPDATA%.
    internal static class AppPaths
    {
        internal const string PortableMarker = "portable.txt";
        private static readonly bool portable = File.Exists(Path.Combine(Folder, PortableMarker));

        internal static string Folder { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
        internal static bool Portable { get { return portable; } }
        internal static string MarkerPath { get { return Path.Combine(Folder, PortableMarker); } }

        internal static string DataFolder
        {
            get
            {
                return portable
                    ? Path.Combine(Folder, "data")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MintClicker");
            }
        }
        internal static string SettingsFile { get { return Path.Combine(DataFolder, "settings.xml"); } }
        internal static string LogFolder { get { return Path.Combine(DataFolder, "logs"); } }
    }
}
