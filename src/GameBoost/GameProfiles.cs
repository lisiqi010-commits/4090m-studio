using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace GameBoost
{
    internal sealed class GameProfile
    {
        internal string GamePath { get; set; }
        internal bool IncludeDefaults { get; set; }
        internal readonly HashSet<string> Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> ExcludedDefaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> ProtectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal GameProfile Clone()
        {
            var copy = new GameProfile { GamePath = GamePath, IncludeDefaults = IncludeDefaults };
            copy.Paths.UnionWith(Paths);
            copy.ExcludedDefaults.UnionWith(ExcludedDefaults);
            copy.ProtectedPaths.UnionWith(ProtectedPaths);
            return copy;
        }
    }

    internal sealed class GameProfiles
    {
        private readonly string file;
        internal bool AutoMode { get; set; }
        internal List<GameProfile> Profiles { get; private set; }

        internal GameProfiles(string folder)
        {
            file = Path.Combine(folder, "game-profiles.xml");
            Profiles = new List<GameProfile>();
            if (!File.Exists(file))
                return;

            XElement root;
            using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                root = XElement.Load(reader);

            if (root.Name != "gameProfiles" || (string)root.Attribute("version") != "1")
                throw new InvalidDataException("游戏配置文件的格式无法识别。" + Environment.NewLine + file);

            AutoMode = ReadFlag(root, "autoMode");
            var gamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement entry in root.Elements("profile"))
            {
                var profile = new GameProfile
                {
                    GamePath = NormalizePath((string)entry.Attribute("game")),
                    IncludeDefaults = ReadFlag(entry, "includeDefaults")
                };
                if (!gamePaths.Add(profile.GamePath))
                    throw new InvalidDataException("游戏配置文件包含重复的游戏路径。" + Environment.NewLine + file);

                LoadPaths(entry, "optimize", profile.Paths);
                LoadPaths(entry, "excludeDefault", profile.ExcludedDefaults);
                LoadPaths(entry, "protect", profile.ProtectedPaths);
                Profiles.Add(profile);
            }
        }

        internal void Save()
        {
            Write(AutoMode, Profiles);
        }

        internal void SaveChanges(bool autoMode, IEnumerable<GameProfile> profiles)
        {
            List<GameProfile> snapshot = profiles.Select(profile => profile.Clone()).ToList();
            Write(autoMode, snapshot);
            AutoMode = autoMode;
            Profiles.Clear();
            Profiles.AddRange(snapshot);
        }

        private void Write(bool autoMode, IEnumerable<GameProfile> profiles)
        {
            var root = new XElement("gameProfiles", new XAttribute("version", "1"), new XAttribute("autoMode", autoMode));
            var gamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameProfile profile in profiles)
            {
                string gamePath = NormalizePath(profile.GamePath);
                if (!gamePaths.Add(gamePath))
                    throw new InvalidDataException("不能重复添加同一个游戏程序。" + Environment.NewLine + gamePath);

                root.Add(new XElement("profile", new XAttribute("game", gamePath),
                    new XAttribute("includeDefaults", profile.IncludeDefaults),
                    WritePaths("optimize", profile.Paths), WritePaths("excludeDefault", profile.ExcludedDefaults),
                    WritePaths("protect", profile.ProtectedPaths)));
            }

            string staged = file + ".new";
            using (var stream = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                root.Save(stream);
                stream.Flush(true);
            }
            if (File.Exists(file))
                File.Replace(staged, file, null);
            else
                File.Move(staged, file);
        }

        private static void LoadPaths(XElement profile, string label, HashSet<string> paths)
        {
            foreach (XElement entry in profile.Elements(label))
                paths.Add(NormalizePath(entry.Value));
        }

        private static IEnumerable<XElement> WritePaths(string label, IEnumerable<string> paths)
        {
            return paths.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path => new XElement(label, path));
        }

        private static bool ReadFlag(XElement element, string name)
        {
            bool flag;
            if (!Boolean.TryParse((string)element.Attribute(name), out flag))
                throw new InvalidDataException("游戏配置文件包含无效的选项。" + Environment.NewLine + name);
            return flag;
        }

        private static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) ||
                !String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("游戏配置需要有效的程序路径。" + Environment.NewLine + path);
            return Path.GetFullPath(path);
        }
    }
}
