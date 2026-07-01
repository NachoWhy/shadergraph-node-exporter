using System.IO;
using UnityEngine;

namespace NachoWhy.SgNodeExporter
{
    static class ProjectPathUtility
    {
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        public static string ToProjectRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var fullPath = Path.GetFullPath(path).Replace('\\', '/');
            var projectRoot = ProjectRoot.Replace('\\', '/').TrimEnd('/');
            if (!fullPath.StartsWith(projectRoot + "/", System.StringComparison.OrdinalIgnoreCase))
                return null;

            return fullPath.Substring(projectRoot.Length + 1);
        }

        public static string GetUniquePath(string path)
        {
            if (!File.Exists(path))
                return path;

            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var fileName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);

            var index = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(directory, $"{fileName}_{index:000}{extension}");
                index++;
            }
            while (File.Exists(candidate));

            return candidate;
        }
    }
}
