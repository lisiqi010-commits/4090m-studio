using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GameBoost
{
    internal sealed class Settings
    {
        private const string ExclusionPrefix = "exclude\t";
        private readonly string file;
        internal readonly HashSet<string> Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> ExcludedDefaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool IncludeDefaults = true;

        internal Settings(string folder)
        {
            file = Path.Combine(folder, "settings.txt");
            if (!File.Exists(file))
                return;

            string[] lines = File.ReadAllLines(file, Encoding.UTF8);
            if (lines.Length == 0 || (lines[0] != "1" && lines[0] != "0"))
                throw new InvalidDataException("无法读取已保存的优化设置。" + Environment.NewLine + file);

            IncludeDefaults = lines[0] == "1";
            for (int index = 1; index < lines.Length; index++)
            {
                bool excluded = lines[index].StartsWith(ExclusionPrefix, StringComparison.Ordinal);
                string path = excluded ? lines[index].Substring(ExclusionPrefix.Length) : lines[index];
                if (Path.IsPathRooted(path) && String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                    (excluded ? ExcludedDefaults : Paths).Add(path);
            }
        }

        internal void Save()
        {
            var lines = new List<string> { IncludeDefaults ? "1" : "0" };
            var sorted = new List<string>(Paths);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            lines.AddRange(sorted);
            var excluded = new List<string>(ExcludedDefaults);
            excluded.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string path in excluded)
                lines.Add(ExclusionPrefix + path);
            string staged = file + ".new";
            File.WriteAllLines(staged, lines, new UTF8Encoding(false));
            if (File.Exists(file))
                File.Replace(staged, file, null);
            else
                File.Move(staged, file);
        }
    }
}
