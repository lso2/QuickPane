using System;
using System.Collections.Generic;
using System.IO;

namespace QuickPane.Services
{
    /// <summary>Default folders used to seed a Quick access group on first run.</summary>
    internal static class KnownFolders
    {
        public static List<string> DefaultSet()
        {
            var list = new List<string>();
            Add(list, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Add(list, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            Add(list, Down(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            Add(list, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            Add(list, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            Add(list, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            Add(list, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
            return list;
        }

        private static string Down(string baseDir, string sub)
        {
            try { return string.IsNullOrEmpty(baseDir) ? null : Path.Combine(baseDir, sub); }
            catch { return null; }
        }

        private static void Add(List<string> list, string path)
        {
            try { if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) list.Add(path); }
            catch { }
        }
    }
}
