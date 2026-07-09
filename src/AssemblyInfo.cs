using System.Reflection;
using System.Runtime.InteropServices;

// Embedded PE version resource — gives the exe proper metadata (Product/Company/Version)
// instead of looking like an anonymous binary, which helps with AV/SmartScreen heuristics.
[assembly: AssemblyTitle("LoadView")]
[assembly: AssemblyProduct("LoadView")]
[assembly: AssemblyDescription("Always-on-top system monitor overlay")]
[assembly: AssemblyCompany("LoadView")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyVersion("2.4.0.0")]
[assembly: AssemblyFileVersion("2.4.0.0")]
[assembly: ComVisible(false)]
