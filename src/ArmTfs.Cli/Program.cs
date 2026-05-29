using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using ArmTfs.Cli.Commands;
using ArmTfs.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// 顶层入口：构建 DI 容器 → 注册命令 → 执行
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddSingleton(_ => TfsConfig.Load().Also(c => c.ApplyEnvironmentOverrides()));

var provider = services.BuildServiceProvider();
var config = provider.GetRequiredService<TfsConfig>();

// ─── 根命令 ────────────────────────────────────────────────────────────────────
var rootCommand = new RootCommand(
    "arm-tfs — Cross-platform TFVC CLI for ARM64 macOS & Windows 11 ARM\n" +
    "Replaces tf.exe using pure REST APIs, no native DLL required.\n\n" +
    "Quick start:\n" +
    "  arm-tfs configure --url https://tfs.company.com/tfs/DefaultCollection --pat <token>\n" +
    "  arm-tfs workspace new --name MyWS --server-path $/Project/Main --local-path .\n" +
    "  arm-tfs get .\n" +
    "  arm-tfs checkout src/MyFile.cs\n" +
    "  arm-tfs checkin -c \"My change\""
);

// global options
var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Show detailed output." };
rootCommand.AddGlobalOption(verboseOption);

// ─── 子命令 ────────────────────────────────────────────────────────────────────
rootCommand.AddCommand(ConfigureCommand.Build(config));
rootCommand.AddCommand(WorkspaceCommand.Build(config));
rootCommand.AddCommand(GetCommand.Build(config));
rootCommand.AddCommand(StatusCommand.Build(config));
rootCommand.AddCommand(CheckoutCommand.Build(config));
rootCommand.AddCommand(AddCommand.Build(config));
rootCommand.AddCommand(UndoCommand.Build(config));
rootCommand.AddCommand(CheckinCommand.Build(config));
rootCommand.AddCommand(HistoryCommand.Build(config));
rootCommand.AddCommand(ShelvesetCommand.Build(config));

var parser = new CommandLineBuilder(rootCommand)
    .UseDefaults()
    .Build();

return await parser.InvokeAsync(args);

// ─── 扩展方法 ──────────────────────────────────────────────────────────────────
static class Extensions
{
    public static T Also<T>(this T obj, Action<T> action) { action(obj); return obj; }
}
