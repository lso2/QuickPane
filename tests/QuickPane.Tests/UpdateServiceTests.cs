// UpdateServiceTests.cs
// Purpose: unit tests for release tag parsing.
// Reason: pure-logic checks that run with no window and no Explorer.
// Defines: UpdateServiceTests.
// Origin: from jprisant's fork, commit 7821277 (MIT, see NOTICE.md).
// Doc: docs/notes/tests/QuickPane.Tests/UpdateServiceTests.cs.md
using System;
using NUnit.Framework;
using QuickPane.Services;

namespace QuickPane.Tests
{
    /// <summary>Tests TryParseTag's contract: the version can be anywhere in the tag string, and
    /// TryParseTag returns false without throwing for a tag with no recognizable version.</summary>
    [TestFixture]
    public class UpdateServiceTests
    {
        [Test]
        public void TryParseTag_PlainVersionTag_Parses()
        {
            Version v;
            bool ok = UpdateService.TryParseTag("v3.9.0", out v);

            Assert.That(ok, Is.True);
            Assert.That(v, Is.EqualTo(new Version(3, 9, 0)));
        }

        [Test]
        public void TryParseTag_VersionBuriedInInstallerName_Parses()
        {
            Version v;
            bool ok = UpdateService.TryParseTag("QuickPaneSetup-3.7.0-release", out v);

            Assert.That(ok, Is.True);
            Assert.That(v, Is.EqualTo(new Version(3, 7, 0)));
        }

        [Test]
        public void TryParseTag_Junk_Fails()
        {
            Version v;
            Assert.That(UpdateService.TryParseTag("junk", out v), Is.False);
        }

        [Test]
        public void TryParseTag_Empty_Fails()
        {
            Version v;
            Assert.That(UpdateService.TryParseTag("", out v), Is.False);
        }

        [Test]
        public void TryParseTag_Null_Fails()
        {
            Version v;
            Assert.That(UpdateService.TryParseTag(null, out v), Is.False);
        }
    }
}
