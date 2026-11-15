// SettingsStoreTests.cs
// Purpose: unit tests for SettingsStore save, load and migration.
// Reason: pure-logic checks that run with no window and no Explorer.
// Defines: SettingsStoreTests.
// Origin: from jprisant's fork, commit 7821277 (MIT, see NOTICE.md).
// Doc: docs/notes/tests/QuickPane.Tests/SettingsStoreTests.cs.md
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using NUnit.Framework;
using QuickPane.Models;
using QuickPane.Services;

namespace QuickPane.Tests
{
    /// <summary>
    /// Tests SettingsStore inside a temp folder, through the internal (string dir) constructor. The
    /// public constructor uses the real %APPDATA%\QuickPane and is not called here. Load() creates the
    /// groups folder, and LoadActiveToTop() copies the active profile's path over the top-level one.
    /// The test settings point GroupsPath and the single profile's GroupsPath inside the temp folder.
    /// </summary>
    [TestFixture]
    public class SettingsStoreTests
    {
        private static void WriteSettingsFile(string dir, string groupsPath)
        {
            var settings = AppSettings.CreateDefault();
            settings.GroupsPath = groupsPath;
            settings.Profiles = new List<Profile>
            {
                new Profile { Name = "Profile 1", GroupsPath = groupsPath, RecentsMaxCount = 15, SidebarWidthPx = 220 }
            };

            var file = Path.Combine(dir, "settings.json");
            using (var fs = File.Create(file))
            {
                var ser = new DataContractJsonSerializer(typeof(AppSettings));
                ser.WriteObject(fs, settings);
            }
        }

        [Test]
        public void Load_ReadsGroupsPathFromTheActiveProfile()
        {
            using (var temp = new TempDir())
            {
                var groupsPath = Path.Combine(temp.Path, "Groups");
                WriteSettingsFile(temp.Path, groupsPath);

                var store = new SettingsStore(temp.Path);
                store.Load();

                Assert.That(store.Current.GroupsPath, Is.EqualTo(groupsPath));
                Assert.That(Directory.Exists(groupsPath), Is.True); // Load() materializes it
            }
        }

        [Test]
        public void SaveThenLoad_RoundTripsAChangedValue()
        {
            using (var temp = new TempDir())
            {
                var groupsPath = Path.Combine(temp.Path, "Groups");
                WriteSettingsFile(temp.Path, groupsPath);

                var store = new SettingsStore(temp.Path);
                store.Load();
                store.Current.RecentsMaxCount = 33;
                store.Save();

                var reloaded = new SettingsStore(temp.Path);
                reloaded.Load();

                Assert.That(reloaded.Current.RecentsMaxCount, Is.EqualTo(33));
            }
        }
    }
}
