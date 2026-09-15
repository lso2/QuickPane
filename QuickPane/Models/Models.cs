using System.Collections.Generic;
using System.Runtime.Serialization;

namespace QuickPane.Models
{
    /// <summary>One pinned folder. Backed by a single .lnk inside a group folder.</summary>
    public sealed class PinnedFolder
    {
        public string DisplayName { get; set; }   // the .lnk name, prefix stripped
        public string TargetPath { get; set; }     // resolved shortcut target
        public string LinkPath { get; set; }        // full path of the .lnk on disk
        public int Order { get; set; }              // from the NNN_ prefix
        public bool Exists { get; set; }            // target currently resolvable
        public bool IsFile { get; set; }            // recent item that is a file, not a folder
    }

    /// <summary>One tab inside a group. Backed by a subfolder of the group folder; its pins are the
    /// .lnk files in that subfolder.</summary>
    public sealed class PinnedTab
    {
        public string Name { get; set; }            // subfolder name, TT_ prefix stripped
        public string FolderPath { get; set; }      // full path of the tab subfolder
        public int Order { get; set; }              // from the TT_ prefix
        public List<PinnedFolder> Items { get; set; } = new List<PinnedFolder>();
    }

    /// <summary>One group. Backed by a real folder under the groups root. A group holds one or more
    /// tabs; with a single tab it renders like a plain group, with several it shows a tab row.</summary>
    public sealed class PinnedGroup
    {
        public string Name { get; set; }            // folder name, NN_ prefix stripped (ordering label)
        public string FolderPath { get; set; }      // full path of the group folder
        public int Order { get; set; }              // from the NN_ prefix
        public bool Expanded { get; set; } = true;
        public int ActiveTab { get; set; }          // index of the visible tab
        public List<PinnedTab> Tabs { get; set; } = new List<PinnedTab>();

        public PinnedTab Active
        {
            get
            {
                if (Tabs.Count == 0) return null;
                int i = ActiveTab;
                if (i < 0) i = 0;
                if (i >= Tabs.Count) i = Tabs.Count - 1;
                return Tabs[i];
            }
        }
    }

    /// <summary>Visibility and ordering for one sidebar section.</summary>
    [DataContract]
    public sealed class SectionSetting
    {
        [DataMember(Name = "type", Order = 0)]
        public string Type { get; set; }            // "groups" | "recents" | "computer"

        [DataMember(Name = "visible", Order = 1)]
        public bool Visible { get; set; } = true;

        [DataMember(Name = "order", Order = 2)]
        public int Order { get; set; }

        // Optional custom header text. When null the section uses its default name.
        [DataMember(Name = "title", Order = 3, EmitDefaultValue = false)]
        public string Title { get; set; }
    }

    /// <summary>One profile: an independent workspace with its own groups folder and pane settings.
    /// The active profile's values are mirrored into the top-level AppSettings fields so the rest of the
    /// app keeps reading GroupsPath / RecentsMaxCount / SidebarWidthPx unchanged.</summary>
    [DataContract]
    public sealed class Profile
    {
        [DataMember(Name = "name", Order = 0)]
        public string Name { get; set; } = "Profile";

        [DataMember(Name = "groupsPath", Order = 1)]
        public string GroupsPath { get; set; } = @"%APPDATA%\QuickPane\Groups";

        [DataMember(Name = "recentsMaxCount", Order = 2)]
        public int RecentsMaxCount { get; set; } = 15;

        [DataMember(Name = "sidebarWidthPx", Order = 3)]
        public int SidebarWidthPx { get; set; } = 220;
    }

    /// <summary>One saved SSH/SFTP connection, mounted as a real drive via SSHFS-Win. Field layout
    /// mirrors PuTTY/KiTTY (host, port, username, private key path) plus WinSCP's keepalive settings.</summary>
    [DataContract]
    public sealed class SshProfile
    {
        [DataMember(Name = "name", Order = 0)]
        public string Name { get; set; } = "New connection";

        [DataMember(Name = "hostname", Order = 1)]
        public string Hostname { get; set; } = "";

        [DataMember(Name = "port", Order = 2)]
        public int Port { get; set; } = 22;

        [DataMember(Name = "username", Order = 3)]
        public string Username { get; set; } = "";

        // "key" or "password", same split as PuTTY's auth panel.
        [DataMember(Name = "authMethod", Order = 4)]
        public string AuthMethod { get; set; } = "key";

        [DataMember(Name = "privateKeyPath", Order = 5, EmitDefaultValue = false)]
        public string PrivateKeyPath { get; set; } = "";

        // Stored in plain text in settings.json, same as a saved WinSCP/PuTTY session password.
        // Key auth avoids this entirely and is the recommended path.
        [DataMember(Name = "password", Order = 6, EmitDefaultValue = false)]
        public string Password { get; set; } = "";

        [DataMember(Name = "remotePath", Order = 7)]
        public string RemotePath { get; set; } = "/";

        [DataMember(Name = "driveLetter", Order = 8)]
        public string DriveLetter { get; set; } = "S:";

        // WinSCP's "Seconds between keepalive packets", passed through as ServerAliveInterval.
        [DataMember(Name = "keepAliveInterval", Order = 9)]
        public int KeepAliveInterval { get; set; } = 10;

        [DataMember(Name = "keepAliveCountMax", Order = 10)]
        public int KeepAliveCountMax { get; set; } = 3;

        [DataMember(Name = "reconnect", Order = 11)]
        public bool Reconnect { get; set; } = true;
    }

    /// <summary>Serialized to %APPDATA%\QuickPane\settings.json.</summary>
    [DataContract]
    public sealed class AppSettings
    {
        [DataMember(Name = "groupsPath", Order = 0)]
        public string GroupsPath { get; set; } = @"%APPDATA%\QuickPane\Groups";

        [DataMember(Name = "sections", Order = 1)]
        public List<SectionSetting> Sections { get; set; }

        // Keyboard navigation of the pane inside a file dialog. Off unless asked for, because it takes a
        // system-wide hotkey and steals a key combination from whatever else wants it.
        [DataMember(Name = "keyboardNav", Order = 40, EmitDefaultValue = false)]
        public bool KeyboardNav { get; set; }

        // The combination that moves focus into the pane. Modifiers are "ctrl", "alt", "shift", "win",
        // joined with "+" and ending in a key name, e.g. "ctrl+shift+Q".
        [DataMember(Name = "keyboardNavHotkey", Order = 41, EmitDefaultValue = false)]
        public string KeyboardNavHotkey { get; set; } = "ctrl+shift+Q";

        // Which apps the Recent Apps section lists: "open" for the ones brought to the front, "dialogs"
        // for the ones whose Save and Open dialogs were used, "both" for either.
        [DataMember(Name = "recentAppsSource", Order = 42, EmitDefaultValue = false)]
        public string RecentAppsSource { get; set; } = "both";

        [DataMember(Name = "recentsMaxCount", Order = 2)]
        public int RecentsMaxCount { get; set; } = 15;

        [DataMember(Name = "sidebarWidthPx", Order = 3)]
        public int SidebarWidthPx { get; set; } = 220;

        // Window attach mode: "inside" embeds the pane inside each Explorer window (shifts content);
        // "beside" floats a pane at the left of each Explorer window (no shift, no flicker).
        [DataMember(Name = "mode", Order = 4, EmitDefaultValue = false)]
        public string Mode { get; set; } = "inside";

        // Optional extra: a screen-edge dock (AppBar) independent of the window pane.
        [DataMember(Name = "desktopDock", Order = 5, EmitDefaultValue = false)]
        public bool DesktopDock { get; set; }

        // When docked, collapse to a 1px strip and slide out on hover.
        [DataMember(Name = "desktopDockAutoHide", Order = 6, EmitDefaultValue = false)]
        public bool DesktopDockAutoHide { get; set; } = true;

        // Show the dock on every virtual desktop instead of only the one it was opened on.
        [DataMember(Name = "desktopDockAllDesktops", Order = 7, EmitDefaultValue = false)]
        public bool DesktopDockAllDesktops { get; set; }

        // Profiles: independent workspaces. The active one's folder/recents/width are mirrored into the
        // top-level fields above. Groups, sections, and dock settings stay global across profiles.
        [DataMember(Name = "profiles", Order = 8, EmitDefaultValue = false)]
        public List<Profile> Profiles { get; set; }

        [DataMember(Name = "activeProfile", Order = 9, EmitDefaultValue = false)]
        public int ActiveProfileIndex { get; set; }

        // Saved SSH/SFTP connections, mounted as real drives via SSHFS-Win.
        [DataMember(Name = "sshProfiles", Order = 12, EmitDefaultValue = false)]
        public List<SshProfile> SshProfiles { get; set; }

        // A row of profile tabs under the QuickPane title that switches the shown profile.
        [DataMember(Name = "showProfileTabs", Order = 10)]
        public bool ShowProfileTabs { get; set; } = true;

        // When on, the profile tab row collapses and slides out when the cursor is over the header.
        [DataMember(Name = "profileTabsAutoHide", Order = 11, EmitDefaultValue = false)]
        public bool ProfileTabsAutoHide { get; set; }

        // GetUninitializedObject skips field initializers, so default the tab row on before members load.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext c) { ShowProfileTabs = true; }

        public Profile ActiveProfile
        {
            get
            {
                if (Profiles == null || Profiles.Count == 0) return null;
                int i = ActiveProfileIndex;
                if (i < 0) i = 0;
                if (i >= Profiles.Count) i = Profiles.Count - 1;
                return Profiles[i];
            }
        }

        /// <summary>Copy the active profile's values into the top-level fields the app reads.</summary>
        public void LoadActiveToTop()
        {
            var a = ActiveProfile;
            if (a == null) return;
            GroupsPath = a.GroupsPath;
            RecentsMaxCount = a.RecentsMaxCount;
            SidebarWidthPx = a.SidebarWidthPx;
        }

        /// <summary>Persist live top-level edits back into the active profile before saving/switching.</summary>
        public void SyncTopToActive()
        {
            var a = ActiveProfile;
            if (a == null) return;
            a.GroupsPath = GroupsPath;
            a.RecentsMaxCount = RecentsMaxCount;
            a.SidebarWidthPx = SidebarWidthPx;
        }

        public static AppSettings CreateDefault()
        {
            return new AppSettings
            {
                GroupsPath = @"%APPDATA%\QuickPane\Groups",
                RecentsMaxCount = 15,
                SidebarWidthPx = 220,
                Sections = new List<SectionSetting>
                {
                    new SectionSetting { Type = "groups",   Visible = true, Order = 0 },
                    new SectionSetting { Type = "recents",  Visible = true, Order = 1 },
                    new SectionSetting { Type = "computer", Visible = true, Order = 2 },
                    new SectionSetting { Type = "network",  Visible = true, Order = 3 },
                    new SectionSetting { Type = "linux",    Visible = true, Order = 4 },
                    new SectionSetting { Type = "ssh",      Visible = true, Order = 5 },
                    new SectionSetting { Type = "search",      Visible = true, Order = -2 },
                    new SectionSetting { Type = "appdefaults", Visible = true, Order = -1 },
                    new SectionSetting { Type = "recentapps",  Visible = true, Order = 6 }
                },
                SshProfiles = new List<SshProfile>()
            };
        }

        /// <summary>Make sure all three sections exist exactly once, preserving stored order/visibility.</summary>
        public void Normalize()
        {
            if (Sections == null) Sections = new List<SectionSetting>();
            EnsureSection("groups", 0);
            EnsureSection("recents", 1);
            EnsureSection("computer", 2);
            EnsureSection("network", 3);
            EnsureSection("linux", 4);
            EnsureSection("ssh", 5);
            // The search box and the per-app folders belong above the pinned groups, so they default to
            // negative orders and sort to the top for anyone whose settings predate them.
            EnsureSection("search", -2);
            EnsureSection("appdefaults", -1);
            EnsureSection("recentapps", 6);
            if (SshProfiles == null) SshProfiles = new List<SshProfile>();
            foreach (var sp in SshProfiles)
            {
                if (string.IsNullOrWhiteSpace(sp.Name)) sp.Name = sp.Hostname;
                if (sp.Port <= 0) sp.Port = 22;
                if (string.IsNullOrWhiteSpace(sp.RemotePath)) sp.RemotePath = "/";
                if (string.IsNullOrWhiteSpace(sp.DriveLetter)) sp.DriveLetter = "S:";
                if (sp.KeepAliveInterval <= 0) sp.KeepAliveInterval = 10;
                if (sp.KeepAliveCountMax <= 0) sp.KeepAliveCountMax = 3;
                var am = (sp.AuthMethod ?? "key").Trim().ToLowerInvariant();
                sp.AuthMethod = am == "password" ? "password" : "key";
            }
            if (string.IsNullOrWhiteSpace(KeyboardNavHotkey)) KeyboardNavHotkey = "ctrl+shift+Q";
            var ras = (RecentAppsSource ?? "").Trim().ToLowerInvariant();
            RecentAppsSource = (ras == "open" || ras == "dialogs") ? ras : "both";
            if (RecentsMaxCount < 5) RecentsMaxCount = 5;
            if (RecentsMaxCount > 50) RecentsMaxCount = 50;
            if (SidebarWidthPx < 160) SidebarWidthPx = 160;
            if (SidebarWidthPx > 400) SidebarWidthPx = 400;
            if (string.IsNullOrWhiteSpace(GroupsPath)) GroupsPath = @"%APPDATA%\QuickPane\Groups";
            var m = (Mode ?? "").Trim().ToLowerInvariant();
            Mode = (m == "beside") ? "beside" : (m == "off") ? "off" : "inside"; // migrate old "inwindow" to "inside"

            // Profiles: migrate a pre-profiles settings file into one profile built from the top fields.
            if (Profiles == null) Profiles = new List<Profile>();
            if (Profiles.Count == 0)
                Profiles.Add(new Profile { Name = "Profile 1", GroupsPath = GroupsPath, RecentsMaxCount = RecentsMaxCount, SidebarWidthPx = SidebarWidthPx });
            foreach (var p in Profiles)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Profile";
                if (string.IsNullOrWhiteSpace(p.GroupsPath)) p.GroupsPath = @"%APPDATA%\QuickPane\Groups";
                if (p.RecentsMaxCount < 5) p.RecentsMaxCount = 5;
                if (p.RecentsMaxCount > 50) p.RecentsMaxCount = 50;
                if (p.SidebarWidthPx < 160) p.SidebarWidthPx = 160;
                if (p.SidebarWidthPx > 400) p.SidebarWidthPx = 400;
            }
            if (ActiveProfileIndex < 0) ActiveProfileIndex = 0;
            if (ActiveProfileIndex >= Profiles.Count) ActiveProfileIndex = Profiles.Count - 1;
        }

        private void EnsureSection(string type, int fallbackOrder)
        {
            if (!Sections.Exists(s => s.Type == type))
                Sections.Add(new SectionSetting { Type = type, Visible = true, Order = fallbackOrder });
        }
    }
}
