// TempDir.cs
// Purpose: a per-test folder under the system temp path.
// Reason: the SettingsStore tests read and write settings.json outside %APPDATA%\QuickPane.
// Defines: TempDir.
// Origin: from jprisant's fork, commit 7821277 (MIT, see NOTICE.md).
// Doc: docs/notes/tests/QuickPane.Tests/TempDir.cs.md
using System;
using System.IO;

namespace QuickPane.Tests
{
    /// <summary>A temp folder for a single test, outside the real %APPDATA%\QuickPane. Each caller
    /// gets its own folder under the system temp path. Dispose deletes that folder and nothing else.</summary>
    internal sealed class TempDir : IDisposable
    {
        public string Path { get; private set; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QuickPane.Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { /* best effort cleanup */ }
        }
    }
}
