using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MintClicker
{
    // One zip with everything needed to understand a support report: version and
    // environment, the current profile library, and the recent log files.
    internal static class Diagnostics
    {
        internal static void Export(string targetPath, string info, string profilesPath, string logFolder)
        {
            using (FileStream file = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
            using (ZipArchive zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                WriteText(zip, "info.txt", info ?? "");
                if (!String.IsNullOrEmpty(profilesPath) && File.Exists(profilesPath))
                    zip.CreateEntryFromFile(profilesPath, "settings.profiles.v2.json");
                if (!String.IsNullOrEmpty(logFolder) && Directory.Exists(logFolder))
                {
                    foreach (string log in Directory.GetFiles(logFolder, "app.log*"))
                        zip.CreateEntryFromFile(log, "logs/" + Path.GetFileName(log));
                }
            }
        }

        private static void WriteText(ZipArchive zip, string name, string text)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using (Stream stream = entry.Open())
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(text);
        }
    }
}
