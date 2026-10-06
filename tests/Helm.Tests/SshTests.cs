using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Ssh;
using Helm.Modules.Ssh.Terminal;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Helm.Tests;

public sealed class SshTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public SshTests() => _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    // ---- Addresses -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("root@example.com", "root", "example.com", 22)]
    [InlineData("  deploy@10.0.0.5:2222 ", "deploy", "10.0.0.5", 2222)]
    [InlineData("ssh hung@vps.example.org", "hung", "vps.example.org", 22)]
    [InlineData("me@[2001:db8::1]:2200", "me", "2001:db8::1", 2200)]
    [InlineData("me@2001:db8::1", "me", "2001:db8::1", 22)]
    [InlineData("first.last@host-1", "first.last", "host-1", 22)]
    public void Addresses_parse(string text, string user, string host, int port)
    {
        Assert.True(SshAddress.TryParse(text, out var u, out var h, out var p));
        Assert.Equal((user, host, port), (u, h, p));
        Assert.True(SshAddress.TryParse(SshAddress.Format(u, h, p), out var u2, out var h2, out var p2));
        Assert.Equal((u, h, p), (u2, h2, p2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("@example.com")]
    [InlineData("root@")]
    [InlineData("root@host:0")]
    [InlineData("root@host:70000")]
    [InlineData("root@host:22x")]
    [InlineData("ro ot@host")]
    [InlineData("root@ho st")]
    [InlineData("root@host;rm -rf")]
    [InlineData("o'brien@host")]
    [InlineData("me@[2001:db8::1")]
    public void Bad_addresses_are_refused(string text) => Assert.False(SshAddress.TryParse(text, out _, out _, out _));

    // ---- Known hosts -----------------------------------------------------------------------------------------------

    [Fact]
    public void Host_keys_are_trusted_on_first_use_and_pinned()
    {
        var key = new SshHostKey("Example.com", 22, "ssh-ed25519", "abc");
        Assert.Equal(HostKeyMatch.Unknown, SshKnownHosts.Check([], key));
        var known = SshKnownHosts.Trust([], key, DateTimeOffset.UnixEpoch);
        Assert.Equal(HostKeyMatch.Trusted, SshKnownHosts.Check(known, key with { Address = "example.com" }));
        // Same server and key type, another key: what an attack in the middle looks like.
        Assert.Equal(HostKeyMatch.Changed, SshKnownHosts.Check(known, key with { Fingerprint = "xyz" }));
        // Another port is another server; another key type is asked about, not refused.
        Assert.Equal(HostKeyMatch.Unknown, SshKnownHosts.Check(known, key with { Port = 2222 }));
        Assert.Equal(HostKeyMatch.Unknown, SshKnownHosts.Check(known, key with { Algorithm = "ecdsa-sha2-nistp256", Fingerprint = "xyz" }));
        // Trusting again replaces the old key of that type rather than keeping both.
        var replaced = SshKnownHosts.Trust(known, key with { Fingerprint = "new" }, DateTimeOffset.UnixEpoch);
        Assert.Single(replaced);
        Assert.Equal(HostKeyMatch.Changed, SshKnownHosts.Check(replaced, key));
    }

    [Theory]
    [InlineData("rsa-sha2-512", "ssh-rsa")]
    [InlineData("rsa-sha2-256", "ssh-rsa")]
    [InlineData("ssh-ed25519", "ssh-ed25519")]
    [InlineData("ssh-ed25519-cert-v01@openssh.com", "ssh-ed25519")]
    [InlineData("ecdsa-sha2-nistp256", "ecdsa-sha2-nistp256")]
    public void Key_types_ignore_the_signature_hash(string algorithm, string type) => Assert.Equal(type, SshKnownHosts.KeyType(algorithm));

    [Theory]
    [InlineData("SHA256:abc+/de==", "abc+/de")]
    [InlineData("abc+/de=", "abc+/de")]
    [InlineData("abc", "abc")]
    public void Fingerprints_are_written_like_OpenSSH(string raw, string normal) => Assert.Equal(normal, SshKnownHosts.NormalizeFingerprint(raw));

    // ---- Terminal buffer -------------------------------------------------------------------------------------------

    [Fact]
    public void The_terminal_buffer_keeps_everything_until_full()
    {
        var buffer = new TerminalBuffer(16);
        buffer.Append("hello "u8);
        buffer.Append("world"u8);
        Assert.Equal("hello world", Encoding.ASCII.GetString(buffer.Snapshot()));
    }

    [Fact]
    public void The_terminal_buffer_drops_old_output_and_replays_from_a_line_start()
    {
        var buffer = new TerminalBuffer(16);
        buffer.Append("line one\nline two\nthree"u8);
        // 23 bytes in 16 keep "e\nline two\nthree"; the cut line is left out.
        Assert.Equal("line two\nthree", Encoding.ASCII.GetString(buffer.Snapshot()));
        buffer.Append("\nfour\n"u8);
        Assert.Equal("three\nfour\n", Encoding.ASCII.GetString(buffer.Snapshot()));
        Assert.Equal(16, buffer.Count);
        buffer.Clear();
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void The_terminal_buffer_wraps_around_many_times()
    {
        var buffer = new TerminalBuffer(10);
        var all = new StringBuilder();
        for (var i = 0; i < 50; i++)
        {
            var piece = $"{i % 10}{i % 7}\n";
            all.Append(piece);
            buffer.Append(Encoding.ASCII.GetBytes(piece));
        }
        var expected = all.ToString()[^10..];
        Assert.Equal(expected[(expected.IndexOf('\n') + 1)..], Encoding.ASCII.GetString(buffer.Snapshot()));
    }

    // ---- This device's key -----------------------------------------------------------------------------------------

    [Fact]
    public void The_device_key_is_ed25519_in_OpenSSH_form()
    {
        var key = new SshDeviceKey(Path.Combine(_dir.Path, "key.bin"), new PlainSecretProtector(), "Hung's PC");
        Assert.False(key.Exists);
        Assert.Equal("", key.PublicKeyLine);
        key.EnsureCreated();
        var parts = key.PublicKeyLine.Split(' ');
        Assert.Equal(3, parts.Length);
        Assert.Equal("ssh-ed25519", parts[0]);
        Assert.Equal("helm@Hung-s-PC", parts[2]);
        var blob = Convert.FromBase64String(parts[1]);
        Assert.Equal(11, BinaryPrimitives.ReadInt32BigEndian(blob));
        Assert.Equal("ssh-ed25519", Encoding.ASCII.GetString(blob, 4, 11));
        Assert.Equal(32, BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(15)));
        Assert.Equal(51, blob.Length);
        Assert.Equal("SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='), key.Fingerprint);

        // The key SSH.NET signs with is the same key.
        using var source = (PrivateKeyFile)key.CreateKeySource();
        var signingKey = Assert.IsType<Renci.SshNet.Security.ED25519Key>(source.Key);
        Assert.Equal(blob[19..], signingKey.PublicKey);
    }

    [Fact]
    public void The_device_key_survives_a_restart_and_a_new_key_replaces_it()
    {
        var file = Path.Combine(_dir.Path, "key.bin");
        var first = new SshDeviceKey(file, new PlainSecretProtector(), "pc");
        first.EnsureCreated();
        var line = first.PublicKeyLine;
        Assert.Equal(line, new SshDeviceKey(file, new PlainSecretProtector(), "pc").PublicKeyLine);
        var changed = 0;
        first.Changed += (_, _) => changed++;
        first.Regenerate();
        Assert.Equal(1, changed);
        Assert.NotEqual(line, first.PublicKeyLine);
        Assert.Equal(first.PublicKeyLine, new SshDeviceKey(file, new PlainSecretProtector(), "pc").PublicKeyLine);
    }

    [Fact]
    public void The_private_key_is_only_written_protected()
    {
        var file = Path.Combine(_dir.Path, "key.bin");
        var protector = new RecordingProtector();
        var key = new SshDeviceKey(file, protector, "pc");
        key.EnsureCreated();
        Assert.Equal("Helm.Ssh.DeviceKey.v1", protector.Purpose);
        var seed = protector.LastPlain!;
        Assert.Equal(32, seed.Length);
        var stored = File.ReadAllBytes(file);
        Assert.Equal(-1, stored.AsSpan().IndexOf(seed));
        Assert.DoesNotContain(".tmp", string.Join(",", Directory.GetFiles(_dir.Path)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_file_from_elsewhere_is_reported_not_replaced()
    {
        var file = Path.Combine(_dir.Path, "key.bin");
        new SshDeviceKey(file, new PlainSecretProtector(), "pc").EnsureCreated();
        var before = File.ReadAllBytes(file);
        var elsewhere = new SshDeviceKey(file, new RefusingProtector(), "pc");
        Assert.False(elsewhere.Exists);
        Assert.Throws<CryptographicException>(elsewhere.EnsureCreated);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData("", "helm@device")]
    [InlineData("DESKTOP-01", "helm@DESKTOP-01")]
    [InlineData("Máy của anh", "helm@M-y-c-a-anh")]
    [InlineData("a'b;c", "helm@a-b-c")]
    public void Key_comments_are_plain(string device, string comment) => Assert.Equal(comment, SshDeviceKey.Comment(device));

    // ---- Installing the key ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ssh-ed25519 AAAA helm@pc'; rm -rf ~; echo '")]
    [InlineData("ssh-ed25519 AAAA\nssh-ed25519 BBBB")]
    [InlineData("ssh-ed25519 AAAA $(id)")]
    [InlineData("ssh-ed25519 AAAA `id`")]
    [InlineData("command=\"sh\" ssh-ed25519 AAAA")]
    [InlineData("")]
    public void Only_plain_public_keys_go_into_the_install_command(string line) => Assert.Throws<ArgumentException>(() => SshKeyInstall.Command(line));

    [SshShellFact]
    public void Installing_the_key_adds_it_once_on_its_own_line()
    {
        var home = Path.Combine(_dir.Path, "home");
        var ssh = Path.Combine(home, ".ssh");
        Directory.CreateDirectory(ssh);
        // An existing key without a final line break must not be glued to ours.
        File.WriteAllText(Path.Combine(ssh, "authorized_keys"), "ssh-ed25519 AAAAexisting other@pc");
        const string line = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOv8 helm@pc";
        RunShell(SshKeyInstall.Command(line), home);
        RunShell(SshKeyInstall.Command(line), home);
        var lines = File.ReadAllText(Path.Combine(ssh, "authorized_keys")).Split('\n');
        Assert.Equal(["ssh-ed25519 AAAAexisting other@pc", line, ""], lines);

        // A fresh account: the folder and the file are created.
        var fresh = Path.Combine(_dir.Path, "fresh");
        Directory.CreateDirectory(fresh);
        RunShell(SshKeyInstall.Command(line), fresh);
        Assert.Equal(line + "\n", File.ReadAllText(Path.Combine(fresh, ".ssh", "authorized_keys")));
    }

    private static void RunShell(string command, string home)
    {
        var start = new ProcessStartInfo(SshShellFactAttribute.Bash!) { RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        start.Environment["HOME"] = home.Replace('\\', '/');
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    // ---- Connections -----------------------------------------------------------------------------------------------

    [Fact]
    public void Retired_algorithms_are_not_offered()
    {
        var info = new ConnectionInfo("example.com", "me", new PasswordAuthenticationMethod("me", "x"));
        SshConnection.Harden(info);
        var all = info.KeyExchangeAlgorithms.Keys.Concat(info.Encryptions.Keys).Concat(info.HmacAlgorithms.Keys).Concat(info.HostKeyAlgorithms.Keys).ToList();
        Assert.DoesNotContain(all, a => a.Contains("sha1", StringComparison.Ordinal) || a.Contains("cbc", StringComparison.Ordinal)
                                        || a.Contains("3des", StringComparison.Ordinal) || a.Contains("md5", StringComparison.Ordinal) || a.StartsWith("ssh-dss", StringComparison.Ordinal));
        Assert.DoesNotContain("ssh-rsa", info.HostKeyAlgorithms.Keys);
        // Modern choices remain.
        Assert.Contains(info.KeyExchangeAlgorithms.Keys, a => a.StartsWith("curve25519", StringComparison.Ordinal));
        Assert.Contains("ssh-ed25519", info.HostKeyAlgorithms.Keys);
        Assert.Contains("rsa-sha2-512", info.HostKeyAlgorithms.Keys);
        Assert.Contains(info.Encryptions.Keys, a => a.Contains("gcm", StringComparison.Ordinal) || a.Contains("chacha20", StringComparison.Ordinal));
        Assert.Contains(info.HmacAlgorithms.Keys, a => a.StartsWith("hmac-sha2-256", StringComparison.Ordinal));
    }

    [Fact]
    public void Password_connections_answer_only_hidden_prompts()
    {
        var key = new SshDeviceKey(Path.Combine(_dir.Path, "key.bin"), new PlainSecretProtector(), "pc");
        var info = SshConnection.Create(new SshHost { User = "me", Address = "example.com", Auth = SshAuthKind.Password }, key, "secret");
        Assert.Equal(["password", "keyboard-interactive"], info.AuthenticationMethods.Select(m => m.Name));
        // A password connection never makes a device key.
        Assert.False(key.Exists);
        var withKey = SshConnection.Create(new SshHost { User = "me", Address = "example.com" }, key, null);
        Assert.Equal(["publickey"], withKey.AuthenticationMethods.Select(m => m.Name));
    }

    [Fact]
    public void Failures_are_described_without_secrets()
    {
        var host = new SshHost { User = "me", Address = "example.com", Port = 2222 };
        Assert.Contains("authorized_keys", SshErrors.Describe(new SshAuthenticationException("Permission denied (publickey)."), host), StringComparison.Ordinal);
        Assert.Contains("user name or password", SshErrors.Describe(new SshAuthenticationException("x"), host with { Auth = SshAuthKind.Password }), StringComparison.Ordinal);
        Assert.Contains("port 2222", SshErrors.Describe(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused), host), StringComparison.Ordinal);
        Assert.Contains("did not answer", SshErrors.Describe(new SshOperationTimeoutException(), host), StringComparison.Ordinal);
    }

    // ---- Paste -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Pastes_with_line_breaks_are_described()
    {
        Assert.Contains("runs as soon as", TerminalView.PasteWarning("ls\n"), StringComparison.Ordinal);
        Assert.Contains("3 lines", TerminalView.PasteWarning("a\nb\nc"), StringComparison.Ordinal);
        Assert.Contains("2 lines", TerminalView.PasteWarning("a\nb\n"), StringComparison.Ordinal);
    }

    // ---- The view model --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Servers_are_added_edited_and_deleted()
    {
        using var vm = NewViewModel(out _);
        Assert.False(vm.HasHosts);
        vm.NewHostCommand.Execute(null);
        Assert.True(vm.IsEditing);
        vm.EditAddress = "not an address";
        vm.SaveEditCommand.Execute(null);
        Assert.NotNull(vm.EditError);
        Assert.False(vm.HasHosts);

        vm.EditAddress = "deploy@example.com:2222";
        vm.EditName = " Web ";
        vm.EditAuthIndex = 1;
        vm.SaveEditCommand.Execute(null);
        Assert.False(vm.IsEditing);
        var row = Assert.Single(vm.Hosts);
        Assert.Same(row, vm.SelectedHost);
        Assert.Equal(("Web", "deploy@example.com:2222", SshAuthKind.Password), (row.Title, row.Target, row.Host.Auth));
        Assert.Equal("deploy@example.com:2222 · Password", row.Detail);
        Assert.True(vm.NeedsPassword);

        vm.EditHostCommand.Execute(row);
        Assert.Equal("Edit server", vm.EditorTitle);
        vm.EditAuthIndex = 0;
        vm.SaveEditCommand.Execute(null);
        Assert.Single(vm.Hosts);
        Assert.Equal(SshAuthKind.DeviceKey, vm.Hosts[0].Host.Auth);
        Assert.False(vm.NeedsPassword);

        // Saved for the next start.
        _settings.Get<SshSettings>(SshIds.ModuleId).Flush();
        using (var again = NewViewModel(out _))
        {
            Assert.Equal("Web", Assert.Single(again.Hosts).Title);
            Assert.Equal(again.Hosts[0], again.SelectedHost);
        }

        await vm.DeleteHostCommand.ExecuteAsync(vm.Hosts[0]);
        Assert.False(vm.HasHosts);
        Assert.Null(vm.SelectedHost);
    }

    [Fact]
    public async Task A_password_is_required_before_connecting_and_never_saved()
    {
        using var vm = NewViewModel(out _);
        vm.EditAddress = "me@127.0.0.1:1";
        vm.EditAuthIndex = 1;
        vm.SaveEditCommand.Execute(null);
        await vm.ConnectAsync("");
        Assert.Contains("password", vm.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(vm.ActiveSession);

        // Port 1 on this machine refuses: the failure is shown, and nothing secret lands in the settings file.
        await vm.ConnectAsync("hunter2-secret");
        Assert.False(vm.IsConnected);
        Assert.NotNull(vm.Message);
        // A session that never printed anything is dropped: the page shows its note, not an empty terminal.
        Assert.Null(vm.ActiveSession);
        Assert.False(vm.HasSession);
        var store = _settings.Get<SshSettings>(SshIds.ModuleId);
        store.Flush();
        Assert.DoesNotContain("hunter2", File.ReadAllText(store.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Copying_the_public_key_makes_it_first()
    {
        using var vm = NewViewModel(out var clipboard);
        Assert.False(vm.HasKey);
        vm.CopyPublicKeyCommand.Execute(null);
        Assert.True(vm.HasKey);
        Assert.StartsWith("ssh-ed25519 ", clipboard.Text, StringComparison.Ordinal);
        Assert.Equal(vm.PublicKey, clipboard.Text);
    }

    private SshViewModel NewViewModel(out MemoryClipboard clipboard)
    {
        clipboard = new MemoryClipboard();
        var key = new SshDeviceKey(Path.Combine(_dir.Path, "settings", "ssh", "device-key.bin"), new PlainSecretProtector(), "pc");
        return new SshViewModel(_settings, key, new InlineUi(), new AcceptDialogs(), clipboard, NullLogger<SshViewModel>.Instance);
    }

    private sealed class RecordingProtector : ISecretProtector
    {
        public string? Purpose { get; private set; }

        public byte[]? LastPlain { get; private set; }

        public byte[] Protect(byte[] data, string purpose)
        {
            Purpose = purpose;
            LastPlain = [.. data];
            return [.. data.Select(b => (byte)(b ^ 0x5A)), 1, 2, 3];
        }

        public byte[] Unprotect(byte[] data, string purpose) => [.. data[..^3].Select(b => (byte)(b ^ 0x5A))];
    }

    private sealed class RefusingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data, string purpose) => data;

        public byte[] Unprotect(byte[] data, string purpose) => throw new CryptographicException("Protected for another user.");
    }
}

/// <summary>Runs where Git's bash is installed (developer machines and the Windows CI runner).</summary>
public sealed class SshShellFactAttribute : FactAttribute
{
    internal static readonly string? Bash = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "bash.exe"),
    }.FirstOrDefault(File.Exists);

    public SshShellFactAttribute()
    {
        if (Bash is null) Skip = "Needs Git's bash to run the authorized_keys command.";
    }
}
