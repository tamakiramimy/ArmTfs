using System.Runtime.CompilerServices;

// Exposes internal helpers to the test project so the pure conflict/content logic can be unit-tested
// without a live TFS server or heavy HTTP mocking.
[assembly: InternalsVisibleTo("ArmTfs.Core.Tests")]
