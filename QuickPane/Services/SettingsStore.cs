using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using QuickPane.Models;

namespace QuickPane.Services
{
    /// <summary>
    /// Loads and saves settings.json. Writes are atomic (temp file then replace) so a crash or
    /// Explorer close mid-write can never leave a truncated file. Raises Changed after every save
    /// so open sidebars can react live with no Apply button.
    /// </summary>
    public sealed class SettingsStore
    {
        public event EventHandler Changed;

        // Raised when the active profile is switched, added, removed, or renamed, so the app can re-point
        // the group store at the new folder and refresh.
        public event EventHandler ProfilesChanged;

        // Raised continuously while the user drags the pane edge, so every sidebar can relayout live
        // without writing settings.json on each pixel. The final value is persisted on drag release.
        public event EventHandler WidthChangedLive;

        public AppSettings Current { get; private set; }

        public void NotifyWidthLive(int widthPx)
        {
            if (widthPx < 160) widthPx = 160;
            if (widthPx > 400) widthPx = 400;
            Current.SidebarWidthPx = widthPx;
            WidthChangedLive?.Invoke(this, EventArgs.Empty);
        }

        private readonly string _dir;
        private readonly string _file;

        public SettingsStore()
        {
            _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPane");
            _file = Path.Combine(_dir, "settings.json");
        }

        /// <summary>Fully expanded groups folder path (environment tokens resolved at read time).</summary>
        public string ExpandedGroupsPath
        {
            get { return Environment.ExpandEnvironmentVariables(Current.GroupsPath); }
        }

        public void Load()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                if (File.Exists(_file))
                {
                    using (var fs = File.OpenRead(_file))
                    {
                        var ser = new DataContractJsonSerializer(typeof(AppSettings));
                        Current = (AppSettings)ser.ReadObject(fs);
                    }
                }
                if (Current == null) Current = AppSettings.CreateDefault();
                Current.Normalize();
                Current.LoadActiveToTop(); // the active profile is the source of truth at load
            }
            catch (Exception ex)
            {
                Log.Error("settings load failed, using defaults", ex);
                Current = AppSettings.CreateDefault();
                Current.Normalize();
                Current.LoadActiveToTop();
            }

            // Make sure the groups directory exists so the first run is not empty.
            try { Directory.CreateDirectory(ExpandedGroupsPath); } catch (Exception ex) { Log.Error("create groups dir", ex); }

            if (!File.Exists(_file)) Save(); // materialize defaults
        }

        public void Save()
        {
            try
            {
                Current.SyncTopToActive(); // persist live top-level edits into the active profile first
                Current.Normalize();
                Directory.CreateDirectory(_dir);
                var tmp = _file + ".tmp";

                using (var ms = new MemoryStream())
                {
                    var settings = new DataContractJsonSerializerSettings { };
                    var ser = new DataContractJsonSerializer(typeof(AppSettings), settings);
                    ser.WriteObject(ms, Current);
                    var bytes = Indent(ms.ToArray());
                    File.WriteAllBytes(tmp, bytes);
                }

                if (File.Exists(_file)) File.Replace(tmp, _file, null);
                else File.Move(tmp, _file);

                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Error("settings save failed", ex);
            }
        }

        // ---- backup / restore -----------------------------------------------
        public void ExportTo(string path)
        {
            Save();
            File.Copy(_file, path, true);
        }

        public bool ImportFrom(string path)
        {
            try
            {
                AppSettings loaded;
                using (var fs = File.OpenRead(path))
                {
                    var ser = new DataContractJsonSerializer(typeof(AppSettings));
                    loaded = (AppSettings)ser.ReadObject(fs);
                }
                if (loaded == null) return false;
                Current = loaded;
                Current.Normalize();
                Current.LoadActiveToTop();
                Save();
                ProfilesChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex) { Log.Error("settings import failed", ex); return false; }
        }

        // ---- profiles --------------------------------------------------------

        /// <summary>Expanded groups folder for any profile.</summary>
        public string ExpandedGroupsPathFor(Profile p)
        {
            return Environment.ExpandEnvironmentVariables(p != null ? p.GroupsPath : Current.GroupsPath);
        }

        public void SwitchProfile(int index)
        {
            if (Current.Profiles == null || index < 0 || index >= Current.Profiles.Count) return;
            if (index == Current.ActiveProfileIndex) return;
            Current.SyncTopToActive();          // keep the profile we are leaving up to date
            Current.ActiveProfileIndex = index;
            Current.LoadActiveToTop();          // bring the new profile's values forward
            Save();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        public Profile AddProfile(string name)
        {
            if (Current.Profiles == null) Current.Profiles = new System.Collections.Generic.List<Profile>();
            if (string.IsNullOrWhiteSpace(name)) name = "Profile " + (Current.Profiles.Count + 1);
            // Each new profile gets its own groups folder so it starts independent.
            string safe = name.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c.ToString(), string.Empty);
            if (string.IsNullOrWhiteSpace(safe)) safe = "Profile" + (Current.Profiles.Count + 1);
            var p = new Profile
            {
                Name = name.Trim(),
                GroupsPath = @"%APPDATA%\QuickPane\Profiles\" + safe + @"\Groups",
                RecentsMaxCount = 15,
                SidebarWidthPx = 220
            };
            Current.Profiles.Add(p);
            Save();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            return p;
        }

        public void RemoveProfile(int index)
        {
            if (Current.Profiles == null || Current.Profiles.Count <= 1) return; // always keep one
            if (index < 0 || index >= Current.Profiles.Count) return;
            Current.Profiles.RemoveAt(index);
            if (Current.ActiveProfileIndex >= Current.Profiles.Count) Current.ActiveProfileIndex = Current.Profiles.Count - 1;
            Current.LoadActiveToTop();
            Save();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RenameProfile(int index, string name)
        {
            if (Current.Profiles == null || index < 0 || index >= Current.Profiles.Count) return;
            if (string.IsNullOrWhiteSpace(name)) return;
            Current.Profiles[index].Name = name.Trim();
            Save();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        // DataContractJsonSerializer emits compact JSON. A light indent keeps the file
        // hand-editable, which matters because groupsPath is meant to be user editable.
        private static byte[] Indent(byte[] compact)
        {
            try
            {
                var s = Encoding.UTF8.GetString(compact);
                var sb = new StringBuilder();
                int depth = 0;
                bool inStr = false;
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '"' && (i == 0 || s[i - 1] != '\\')) inStr = !inStr;
                    if (inStr) { sb.Append(c); continue; }
                    switch (c)
                    {
                        case '{':
                        case '[':
                            sb.Append(c).Append('\n').Append(new string(' ', ++depth * 2));
                            break;
                        case '}':
                        case ']':
                            sb.Append('\n').Append(new string(' ', --depth * 2)).Append(c);
                            break;
                        case ',':
                            sb.Append(c).Append('\n').Append(new string(' ', depth * 2));
                            break;
                        case ':':
                            sb.Append(": ");
                            break;
                        default:
                            sb.Append(c);
                            break;
                    }
                }
                return Encoding.UTF8.GetBytes(sb.ToString());
            }
            catch
            {
                return compact;
            }
        }
    }
}
