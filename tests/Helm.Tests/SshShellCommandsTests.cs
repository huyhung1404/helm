using Helm.Modules.Ssh;

namespace Helm.Tests;

/// <summary>Which AI agents' shell commands count as status reads that may run without asking.</summary>
public sealed class SshShellCommandsTests
{
    [Theory]
    [InlineData("uptime")]
    [InlineData("df -h")]
    [InlineData("  free -m ")]
    [InlineData("ps aux")]
    [InlineData("ls -la /var/log")]
    [InlineData("date +%F")]
    [InlineData("hostname -I")]
    [InlineData("pm2 ls")]
    [InlineData("systemctl status nginx")]
    [InlineData("systemctl --failed")]
    [InlineData("docker ps -a")]
    [InlineData("docker stats --no-stream")]
    [InlineData("git status")]
    [InlineData("git log --oneline -5")]
    [InlineData("git branch -a")]
    public void Plain_status_reads_are_status_commands(string command) => Assert.True(SshShellCommands.IsStatusCommand(command));

    [Theory]
    // Anything the shell would interpret.
    [InlineData("df -h; rm -rf ~")]
    [InlineData("df -h && reboot")]
    [InlineData("ls | sh")]
    [InlineData("ls > /etc/passwd")]
    [InlineData("ls $(rm -rf ~)")]
    [InlineData("ls `reboot`")]
    [InlineData("ls ~/*")]
    [InlineData("ls 'x'")]
    [InlineData("ls\nreboot")]
    [InlineData("ls & reboot")]
    // Programs not on the list, or that read secrets.
    [InlineData("cat .env")]
    [InlineData("tail -n 50 app.log")]
    [InlineData("rm -rf /tmp/x")]
    [InlineData("pm2 jlist")]
    [InlineData("pm2 restart web")]
    [InlineData("sudo df -h")]
    [InlineData("env")]
    // Options and subcommands that change something.
    [InlineData("hostname evil")]
    [InlineData("date -s 2020-01-01")]
    [InlineData("systemctl restart nginx")]
    [InlineData("systemctl status nginx -H other")]
    [InlineData("docker rm web")]
    [InlineData("docker ps --format=x -a")]
    [InlineData("git log --output=/tmp/x")]
    [InlineData("git log --ext-diff")]
    [InlineData("git branch -D main")]
    [InlineData("git branch -vD main")]
    [InlineData("git branch new-branch")]
    [InlineData("git -c core.pager=sh log")]
    [InlineData("git push")]
    [InlineData("")]
    public void Anything_else_is_asked(string command) => Assert.False(SshShellCommands.IsStatusCommand(command));
}
