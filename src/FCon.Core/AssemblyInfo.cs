using System.Runtime.CompilerServices;

// The smoke tool exercises a few internal helpers directly - response parsing and the
// like - which are implementation details that should not become public API just to be
// testable.
[assembly: InternalsVisibleTo("FCon.Smoke")]
[assembly: InternalsVisibleTo("FCon.Core.Tests")]
