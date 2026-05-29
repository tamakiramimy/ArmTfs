using System.CommandLine;
using ArmTfs.Core.Config;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs configure — 设置 TFS 服务器连接信息</summary>
public static class ConfigureCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("configure", "Configure TFS server connection settings.");

        var urlOpt = new Option<string?>("--url") { Description = "TFS collection URL (e.g. https://tfs.company.com/tfs/DefaultCollection)" };
        var patOpt = new Option<string?>("--pat") { Description = "Personal Access Token (recommended for ARM64/macOS)" };
        var userOpt = new Option<string?>("--username") { Description = "Windows username (NTLM, alternative to PAT)" };
        var passOpt = new Option<string?>("--password") { Description = "Windows password (NTLM, alternative to PAT)" };
        var displayOpt = new Option<string?>("--display-name") { Description = "Your display name for checkin attribution" };
        var showOpt = new Option<bool>("--show") { Description = "Show current configuration" };

        cmd.AddOption(urlOpt);
        cmd.AddOption(patOpt);
        cmd.AddOption(userOpt);
        cmd.AddOption(passOpt);
        cmd.AddOption(displayOpt);
        cmd.AddOption(showOpt);

        cmd.SetHandler((url, pat, user, pass, display, show) =>
        {
            if (show)
            {
                Console.WriteLine($"Config file : {TfsConfig.DefaultConfigPath}");
                Console.WriteLine($"Server URL  : {config.ServerUrl ?? "(not set)"}");
                Console.WriteLine($"Auth        : {(string.IsNullOrEmpty(config.PersonalAccessToken) ? (string.IsNullOrEmpty(config.Username) ? "none" : $"Username: {config.Username}") : "PAT (set)")}");
                Console.WriteLine($"Display Name: {config.UserDisplayName ?? "(not set)"}");
                return;
            }

            if (url is not null) config.ServerUrl = url;
            if (pat is not null) config.PersonalAccessToken = pat;
            if (user is not null) config.Username = user;
            if (pass is not null) config.Password = pass;
            if (display is not null) config.UserDisplayName = display;

            config.Save();
            Console.WriteLine($"Configuration saved to {TfsConfig.DefaultConfigPath}");
        }, urlOpt, patOpt, userOpt, passOpt, displayOpt, showOpt);

        return cmd;
    }
}
