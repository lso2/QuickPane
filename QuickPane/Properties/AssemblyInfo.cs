// AssemblyInfo.cs
// Purpose: the assembly's name, version, company and copyright, and the test project's access to internals.
// Reason: Windows reads the version and company from these attributes, and the installer and README have the same version.
// Defines: assembly attributes only.
// Doc: docs/notes/QuickPane/Properties/AssemblyInfo.cs.md
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;

// The test project reads internal members (SettingsStore's folder constructor, TryParseTag,
// BuildCommandLine) with no reflection. The assembly has no strong name, and no public key is needed.
[assembly: InternalsVisibleTo("QuickPane.Tests")]

[assembly: AssemblyTitle("QuickPane")]
[assembly: AssemblyDescription("Explorer sidebar replacement for Windows 10 LTSC")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("PlexPixel")]
[assembly: AssemblyProduct("QuickPane")]
[assembly: AssemblyCopyright("Copyright (C) 2026 PlexPixel")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]

// WPF theme/location info. Generic + source assembly fallback for ResourceDictionary lookups.
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly)]

[assembly: AssemblyVersion("3.12.0.0")]
[assembly: AssemblyFileVersion("3.12.0.0")]
