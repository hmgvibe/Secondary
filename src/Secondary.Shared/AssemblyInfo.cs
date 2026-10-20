using System.Runtime.CompilerServices;

// Shared services keep their internal surface; only the two shells and their tests use it.
[assembly: InternalsVisibleTo("Secondary.App")]
[assembly: InternalsVisibleTo("Secondary.UiTests")]
[assembly: InternalsVisibleTo("secondary")]
[assembly: InternalsVisibleTo("Secondary.Linux.Tests")]
[assembly: InternalsVisibleTo("Secondary.Tests")]
