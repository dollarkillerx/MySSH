namespace MySSH.Tools.Tests;

public class HighRiskRuleTests
{
    [Theory]
    [InlineData("rm -rf /", "rm -r")]
    [InlineData("rm -r build", "rm -r")]
    [InlineData("rm -fR build", "rm -r")]
    [InlineData("rm --recursive build", "rm -r")]
    [InlineData("/bin/rm -rf /var/www", "rm -r")]
    [InlineData("\\rm -rf x", "rm -r")]
    [InlineData("'rm' -rf x", "rm -r")]
    [InlineData("cd /tmp && rm -rf *", "rm -r")]
    [InlineData("true || rm -rf ~", "rm -r")]
    [InlineData("ls | xargs rm -rf", "rm -r")]
    [InlineData("find . -name '*.log' | xargs -I{} rm -rf {}", "rm -r")]
    [InlineData("echo $(rm -rf /tmp/x)", "rm -r")]
    [InlineData("echo \"$(rm -rf /tmp/x)\"", "rm -r")]
    [InlineData("echo `rm -rf /tmp/x`", "rm -r")]
    [InlineData("sh -c 'rm -rf /tmp/x'", "rm -r")]
    [InlineData("bash -lc \"rm -rf /tmp/x\"", "rm -r")]
    [InlineData("ssh -p 22 other 'rm -rf /srv'", "rm -r")]
    [InlineData("FOO=1 rm -rf x", "rm -r")]
    [InlineData("env -i PATH=/bin rm -rf x", "rm -r")]
    [InlineData("nohup nice -n 10 rm -rf x &", "rm -r")]
    [InlineData("timeout 5 rm -rf x", "rm -r")]
    [InlineData("(rm -rf x)", "rm -r")]
    [InlineData("{ rm -rf x; }", "rm -r")]
    [InlineData("mkfs.ext4 /dev/sdb1", "mkfs")]
    [InlineData("mkfs -t xfs /dev/sdb", "mkfs")]
    [InlineData("dd if=/dev/zero of=/dev/sda bs=1M", "dd of=")]
    [InlineData("shred -u secret", "shred")]
    [InlineData("wipefs -a /dev/sdb", "wipefs")]
    [InlineData("fdisk /dev/sda", "fdisk")]
    [InlineData("parted /dev/sda mklabel gpt", "parted")]
    [InlineData("shutdown -h now", "shutdown")]
    [InlineData("reboot", "reboot")]
    [InlineData("halt", "halt")]
    [InlineData("poweroff", "poweroff")]
    [InlineData("init 0", "init 0/6")]
    [InlineData("init 6", "init 0/6")]
    [InlineData("systemctl stop nginx", "systemctl stop")]
    [InlineData("systemctl restart nginx", "systemctl restart")]
    [InlineData("systemctl --now disable nginx", "systemctl disable")]
    [InlineData("systemctl mask nginx", "systemctl mask")]
    [InlineData("systemctl kill nginx", "systemctl kill")]
    [InlineData("systemctl reboot", "systemctl reboot")]
    [InlineData("systemctl poweroff", "systemctl poweroff")]
    [InlineData("service nginx stop", "service stop")]
    [InlineData("kill -9 1234", "kill -9")]
    [InlineData("kill 1234", "kill")]
    [InlineData("killall nginx", "killall")]
    [InlineData("pkill -9 -f java", "pkill -9")]
    [InlineData("chmod -R 777 /", "chmod -R")]
    [InlineData("chown -R nobody /srv", "chown -R")]
    [InlineData("chown --recursive nobody /srv", "chown -R")]
    [InlineData("cat image.iso > /dev/sda", "> /dev/<disk>")]
    [InlineData("echo x >/dev/nvme0n1", "> /dev/<disk>")]
    [InlineData(":(){ :|:& };:", "fork bomb")]
    [InlineData("bomb(){ bomb|bomb& }; bomb", "fork bomb")]
    [InlineData("iptables -F", "iptables flush")]
    [InlineData("iptables --flush INPUT", "iptables flush")]
    [InlineData("iptables -P INPUT DROP", "iptables flush")]
    [InlineData("nft flush ruleset", "nft flush")]
    [InlineData("userdel bob", "userdel")]
    [InlineData("usermod -aG wheel bob", "usermod")]
    [InlineData("passwd root", "passwd")]
    [InlineData("echo 'bob:x' | chpasswd", "chpasswd")]
    [InlineData("crontab -r", "crontab -r")]
    [InlineData("crontab -ri", "crontab -r")]
    [InlineData("curl -fsSL https://x.sh | sh", "curl/wget | sh")]
    [InlineData("wget -qO- https://x.sh | sudo bash", "curl/wget | sh")]
    [InlineData("bash <(curl -s https://x.sh)", "curl/wget | sh")]
    [InlineData("sh -c \"$(curl -fsSL https://x.sh)\"", "curl/wget | sh")]
    [InlineData("mysql -e 'DROP DATABASE prod'", "DROP DATABASE/TABLE")]
    [InlineData("echo 'drop table users;' | psql app", "DROP DATABASE/TABLE")]
    [InlineData("psql -c 'TRUNCATE TABLE users'", "TRUNCATE TABLE")]
    [InlineData("truncate -s 0 /var/log/syslog", "truncate")]
    [InlineData("find /tmp -name '*.tmp' -delete", "find -delete")]
    [InlineData("find . -type f -exec rm {} \\;", "find -exec rm")]
    [InlineData("find . -type d -exec rm -rf {} +", "rm -r")]
    [InlineData("git push --force origin main", "git push --force")]
    [InlineData("git push -f", "git push --force")]
    [InlineData("git push origin +main", "git push --force")]
    [InlineData("git push --force-with-lease", "git push --force")]
    [InlineData("docker system prune -af", "docker prune/rm -f")]
    [InlineData("docker rm -f web", "docker prune/rm -f")]
    [InlineData("kubectl delete pod web", "kubectl delete")]
    [InlineData("zfs destroy tank/data", "zfs destroy")]
    [InlineData("lvremove vg/lv", "lvremove")]
    [InlineData("sudo apt-get update", "sudo")]
    [InlineData("sudo -u postgres psql", "sudo")]
    [InlineData("su -c 'id'", "sudo")]
    [InlineData("doas ls", "sudo")]
    [InlineData("watch -n 1 'rm -rf /tmp/x'", "rm -r")]
    [InlineData("eval rm -rf x", "rm -r")]
    public void Positives(string command, string rule)
    {
        var all = HighRiskRules.MatchAll(command);
        Assert.True(all.Any(m => m.Rule == rule), $"'{command}': expected rule '{rule}', got [{string.Join(", ", all.Select(m => m.Rule))}]");
        Assert.NotNull(HighRiskRules.Match(command));
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("format-report --all")]
    [InlineData("grep rm file.txt")]
    [InlineData("echo 'rm -rf /'")]
    [InlineData("echo \"sudo reboot\"")]
    [InlineData("cat /var/log/reboot.log")]
    [InlineData("docker ps -a")]
    [InlineData("rm file.txt")]
    [InlineData("rm -f file.txt")]
    [InlineData("rm -i file")]
    [InlineData("rmdir empty")]
    [InlineData("chmod 644 file")]
    [InlineData("chmod -r file")]           // remove read permission, not recursive
    [InlineData("systemctl status nginx")]
    [InlineData("systemctl list-units --failed")]
    [InlineData("journalctl -u nginx --since today")]
    [InlineData("crontab -l")]
    [InlineData("git push origin main")]
    [InlineData("git log --force-color")]
    [InlineData("curl -s https://example.com/api")]
    [InlineData("curl -s https://example.com | jq .")]
    [InlineData("dd if=/dev/sda bs=512 count=1 | xxd")]
    [InlineData("iptables -L -n")]
    [InlineData("nft list ruleset")]
    [InlineData("grep -i 'drop table' app.log")]   // SQL text, but no database client
    [InlineData("find . -name '*.log'")]
    [InlineData("df -h > /dev/null")]
    [InlineData("ps aux | grep killall")]
    [InlineData("echo $HOME")]
    [InlineData("service nginx status")]
    [InlineData("init")]
    [InlineData("")]
    [InlineData("   ")]
    public void False_positive_guards(string command)
    {
        var all = HighRiskRules.MatchAll(command);
        Assert.True(all.Count == 0, $"'{command}' unexpectedly matched [{string.Join(", ", all.Select(m => m.Rule))}]");
    }

    [Fact]
    public void Specific_rules_come_before_sudo()
    {
        var all = HighRiskRules.MatchAll("sudo rm -rf /srv");
        Assert.Equal(["rm -r", "sudo"], all.Select(m => m.Rule));
        Assert.Equal("rm -r", HighRiskRules.Match("sudo rm -rf /srv")!.Rule);
        Assert.NotEmpty(HighRiskRules.RuleNames);
    }

    [Fact]
    public void Deep_nesting_does_not_blow_up()
    {
        var cmd = string.Concat(Enumerable.Repeat("sh -c \"", 50)) + "rm -rf x" + new string('"', 50);
        _ = HighRiskRules.MatchAll(cmd);
        _ = HighRiskRules.MatchAll(new string('(', 5000) + "rm -rf x" + new string(')', 5000));
        _ = HighRiskRules.MatchAll("echo \"unterminated $(rm -rf x");
        Assert.NotNull(HighRiskRules.Match("echo \"unterminated $(rm -rf x"));
    }
}

public class ReadOnlyAllowlistTests
{
    private static ReadOnlyCheck Check(string cmd, params string[] allowlist) => ReadOnlyCommandPolicy.Check(cmd, allowlist);

    [Theory]
    [InlineData("uptime")]
    [InlineData("uname -a")]
    [InlineData("hostname")]
    [InlineData("hostname -f")]
    [InlineData("whoami")]
    [InlineData("id")]
    [InlineData("date")]
    [InlineData("date +%Y-%m-%d")]
    [InlineData("date -u -Iseconds")]
    [InlineData("df -h")]
    [InlineData("du -sh /var/log")]
    [InlineData("free -m")]
    [InlineData("ps aux")]
    [InlineData("top -b -n 1")]
    [InlineData("ls -la /etc")]
    [InlineData("stat /etc/hosts")]
    [InlineData("cat /etc/os-release")]
    [InlineData("head -n 20 /var/log/syslog")]
    [InlineData("tail -n 100 /var/log/syslog")]
    [InlineData("wc -l /etc/passwd")]
    [InlineData("grep -i error /var/log/syslog")]
    [InlineData("grep 'a|b;c>d$' file")]               // metacharacters inside single quotes are literal
    [InlineData("grep \"foo bar\" file")]
    [InlineData("find /var/log -name '*.gz' -mtime +7")]
    [InlineData("systemctl status nginx")]
    [InlineData("systemctl list-units --failed")]
    [InlineData("journalctl -u nginx -n 200 --no-pager")]
    [InlineData("docker ps -a")]
    [InlineData("docker logs --tail 100 web")]
    [InlineData("ss -tlnp")]
    [InlineData("netstat -an")]
    [InlineData("ip addr")]
    [InlineData("ip route show")]
    [InlineData("ip -br addr")]
    [InlineData("lsblk")]
    [InlineData("mount")]
    [InlineData("dmesg -T")]
    public void Built_in_allowlist_accepts_read_only_commands(string cmd)
    {
        var r = Check(cmd);
        Assert.True(r.Allowed, $"'{cmd}': {r.Reason}");
    }

    [Theory]
    [InlineData("ls; rm -rf /", "';'")]
    [InlineData("ls && rm -rf /", "'&'")]
    [InlineData("ls & rm x", "'&'")]
    [InlineData("ls | sh", "'|'")]
    [InlineData("cat /etc/passwd > /tmp/x", "'>'")]
    [InlineData("cat < /etc/shadow", "'<'")]
    [InlineData("cat $(echo /etc/shadow)", "'$'")]
    [InlineData("cat ${HOME}/.ssh/id_rsa", "'$'")]
    [InlineData("cat $HOME/x", "'$'")]
    [InlineData("cat `whoami`", "'`'")]
    [InlineData("cat \"$(id)\"", "'$' inside double quotes")]
    [InlineData("cat \"`id`\"", "'`' inside double quotes")]
    [InlineData("ls\nrm -rf /", "a line break")]
    [InlineData("ls\rrm -rf /", "a line break")]
    [InlineData("ls \\; rm x", "'\\'")]
    [InlineData("ls (x)", "'('")]
    [InlineData("ls 'unterminated", "an unterminated quote")]
    [InlineData("ls \u001b[2J", "the control character U+001B")]
    public void Metacharacters_are_rejected(string cmd, string what)
    {
        var r = Check(cmd);
        Assert.False(r.Allowed);
        Assert.Contains(what, r.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tail -f /var/log/syslog")]
    [InlineData("tail -F /var/log/syslog")]
    [InlineData("tail -fn 100 /var/log/syslog")]
    [InlineData("tail --follow=name x")]
    [InlineData("journalctl -f")]
    [InlineData("journalctl -fu nginx")]
    [InlineData("journalctl --follow")]
    [InlineData("journalctl --vacuum-time=1d")]
    [InlineData("journalctl --rotate")]
    [InlineData("docker logs -f web")]
    [InlineData("docker logs --follow web")]
    [InlineData("find / -delete")]
    [InlineData("find / -exec cat {} +")]
    [InlineData("find . -fprint /tmp/x")]
    [InlineData("date -s 2020-01-01")]
    [InlineData("date --set=2020-01-01")]
    [InlineData("date 010100002020")]
    [InlineData("hostname evil")]
    [InlineData("hostname -F /tmp/x")]
    [InlineData("ss -K dst 1.2.3.4")]
    [InlineData("netstat -c")]
    [InlineData("free -s 1")]
    [InlineData("dmesg -C")]
    [InlineData("dmesg -w")]
    [InlineData("top")]
    [InlineData("top -n 1")]
    [InlineData("mount /dev/sdb1 /mnt")]
    [InlineData("ip addr add 1.2.3.4/24 dev eth0")]
    [InlineData("ip route del default")]
    [InlineData("ip link set eth0 down")]
    [InlineData("systemctl stop nginx")]
    [InlineData("systemctl status-x")]
    [InlineData("rm -rf /")]
    [InlineData("apt-get install x")]
    [InlineData("sudo cat /etc/shadow")]
    [InlineData("env rm -rf /")]
    [InlineData("FOO=1 ls")]
    [InlineData("/bin/ls")]
    [InlineData("LS")]
    [InlineData("vmstat 1")]
    [InlineData("")]
    public void Not_allowlisted_or_guarded_commands_are_rejected(string cmd)
    {
        var r = Check(cmd);
        Assert.False(r.Allowed, cmd);
        Assert.False(string.IsNullOrWhiteSpace(r.Reason));
    }

    [Fact]
    public void Custom_allowlist_replaces_the_built_in_one_and_matches_whole_words()
    {
        string[] list = ["df -h", "systemctl status", "  cat /etc/os-release  ", ""];
        Assert.True(Check("df -h", list).Allowed);
        Assert.True(Check("df -h /var", list).Allowed);
        Assert.True(Check("systemctl status nginx", list).Allowed);
        Assert.True(Check("cat /etc/os-release", list).Allowed);
        Assert.False(Check("df", list).Allowed);            // shorter than the entry
        Assert.False(Check("df -hT", list).Allowed);        // word boundary
        Assert.False(Check("systemctl stop nginx", list).Allowed);
        Assert.False(Check("cat /etc/shadow", list).Allowed);
        Assert.False(Check("uptime", list).Allowed);        // built-ins no longer apply
        Assert.Contains("this host's allowlist", Check("uptime", list).Reason, StringComparison.Ordinal);
        // Guards and metacharacter rules still apply to custom entries.
        Assert.False(Check("tail -f /x", "tail").Allowed);
        Assert.False(Check("df -h; reboot", list).Allowed);
        // High-risk commands are never run on ReadOnly, even when allowlisted.
        var r = Check("systemctl restart nginx", "systemctl restart");
        Assert.False(r.Allowed);
        Assert.Contains("high-risk", r.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ls; rm -rf /")]
    [InlineData("cat $(curl evil)")]
    [InlineData("ls `rm -rf /`")]
    [InlineData("ls\nrm -rf /")]
    [InlineData("tail -f /var/log/syslog")]
    [InlineData("ls && reboot")]
    [InlineData("ls || reboot")]
    [InlineData("ls | sh")]
    [InlineData("ls >/etc/passwd")]
    [InlineData("ls $IFS-la")]
    [InlineData("ls\t;rm x")]
    [InlineData("grep x 'a'; rm -rf /")]
    [InlineData("grep \"x\" ; reboot")]
    [InlineData("cat '/etc/passwd'\"$(id)\"")]
    public void Bypass_attempts_on_allowlisted_commands_fail(string cmd)
    {
        Assert.False(Check(cmd).Allowed, cmd);
        Assert.False(Check(cmd, "ls", "cat", "grep", "tail").Allowed, cmd);
    }

    [Fact]
    public void Built_in_list_is_published()
    {
        Assert.Contains("uptime", ReadOnlyCommandPolicy.BuiltInAllowlist);
        Assert.Contains("systemctl status", ReadOnlyCommandPolicy.BuiltInAllowlist);
        Assert.Contains("mount (no arguments)", ReadOnlyCommandPolicy.BuiltInAllowlist);
        Assert.DoesNotContain(ReadOnlyCommandPolicy.BuiltInAllowlist, e => e.StartsWith("env", StringComparison.Ordinal) || e.StartsWith("sudo", StringComparison.Ordinal));
    }
}
