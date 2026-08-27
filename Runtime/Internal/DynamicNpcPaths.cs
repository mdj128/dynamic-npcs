using System;
using System.IO;
using UnityEngine;

namespace DynamicNpcs
{
    /// <summary>Resolves the package's path conventions: relative paths live under StreamingAssets.</summary>
    public static class DynamicNpcPaths
    {
        public static string Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
                return path;
            return Path.Combine(Application.streamingAssetsPath, path);
        }

        public static string ResolveExecutable(string path)
        {
            string resolved = Resolve(path);
            if (string.IsNullOrWhiteSpace(resolved) || File.Exists(resolved))
                return resolved;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (!resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(resolved + ".exe"))
                return resolved + ".exe";
#endif
            return resolved;
        }
    }
}
