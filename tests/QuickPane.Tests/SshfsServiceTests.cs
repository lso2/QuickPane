// SshfsServiceTests.cs
// Purpose: unit tests for sshfs command-line quoting.
// Reason: pure-logic checks that run with no window and no Explorer.
// Defines: SshfsServiceTests.
// Origin: from jprisant's fork, commit 7821277 (MIT, see NOTICE.md).
// Doc: docs/notes/tests/QuickPane.Tests/SshfsServiceTests.cs.md
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using QuickPane.Services;

namespace QuickPane.Tests
{
    /// <summary>
    /// Tests BuildCommandLine's quoting contract: Windows parses an argument with a space or an
    /// embedded quote back as the same single argument. An SSH connection name with a quote in it
    /// cannot close its own quote early and add an extra -o option.
    /// </summary>
    [TestFixture]
    public class SshfsServiceTests
    {
        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        // CommandLineToArgvW parses argv[0] (the program name) under different rules than the rest of
        // the command line. A dummy program token is prepended and then dropped from the result, and
        // the returned array has only the arguments BuildCommandLine produced.
        private static string[] ParseCommandLine(string commandLine)
        {
            int argc;
            IntPtr argv = CommandLineToArgvW("x " + commandLine, out argc);
            if (argv == IntPtr.Zero) throw new InvalidOperationException("CommandLineToArgvW failed");
            try
            {
                var result = new string[argc - 1];
                for (int i = 1; i < argc; i++)
                {
                    IntPtr p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                    result[i - 1] = Marshal.PtrToStringUni(p);
                }
                return result;
            }
            finally { LocalFree(argv); }
        }

        [Test]
        public void BuildCommandLine_PlainArguments_RoundTrip()
        {
            var args = new List<string> { "-p", "22", "user@host:/", "S:" };
            var cmd = SshfsService.BuildCommandLine(args);
            Assert.That(ParseCommandLine(cmd), Is.EqualTo(args.ToArray()));
        }

        [Test]
        public void BuildCommandLine_ArgumentWithSpace_StaysOneArgument()
        {
            var args = new List<string> { "-o", @"IdentityFile=C:\Demo\Keys\Test User\id_ed25519" };
            var cmd = SshfsService.BuildCommandLine(args);
            Assert.That(ParseCommandLine(cmd), Is.EqualTo(args.ToArray()));
        }

        [Test]
        public void BuildCommandLine_ArgumentWithEmbeddedQuote_EscapesAndRoundTrips()
        {
            var args = new List<string> { "volname=Bob\"s Server" };
            var cmd = SshfsService.BuildCommandLine(args);
            Assert.That(ParseCommandLine(cmd), Is.EqualTo(args.ToArray()));
        }

        [Test]
        public void BuildCommandLine_ConnectionNameCannotInjectExtraOption()
        {
            // A connection name crafted to look like it closes its quote and opens a new
            // -o option must still come back as one argument, not split into "-o" and a value.
            var hostile = new List<string> { "myconn\" -o ProxyCommand=calc.exe" };
            var cmd = SshfsService.BuildCommandLine(hostile);
            var parsed = ParseCommandLine(cmd);

            Assert.That(parsed.Length, Is.EqualTo(1));
            Assert.That(parsed[0], Is.EqualTo(hostile[0]));
        }

        [Test]
        public void BuildCommandLine_TrailingBackslashesBeforeClosingQuote_Survive()
        {
            // The argument has a space for quoting and a trailing backslash for testing the backslash
            // doubling before the closing quote.
            var args = new List<string> { @"C:\Demo\Test User\" };
            var cmd = SshfsService.BuildCommandLine(args);
            Assert.That(ParseCommandLine(cmd), Is.EqualTo(args.ToArray()));
        }
    }
}
