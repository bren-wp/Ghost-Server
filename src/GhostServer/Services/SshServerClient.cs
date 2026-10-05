using System.IO;
using System.Globalization;
using System.Security;
using GhostServer.Models;
using Renci.SshNet;

namespace GhostServer.Services;

public static class SshServerClient
{
    public static Task<ConnectionProbe> ProbeAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var client = CreateClient(profile, secret, out var hostKeyState);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                client.Connect();
                return new ConnectionProbe
                {
                    Connected = client.IsConnected,
                    PresentedFingerprint = hostKeyState.Fingerprint,
                    PresentedHostKeyAlgorithm = hostKeyState.Algorithm
                };
            }
            catch (Exception) when (hostKeyState.Fingerprint is not null &&
                                    string.IsNullOrWhiteSpace(profile.HostKeyFingerprint))
            {
                return new ConnectionProbe
                {
                    Connected = false,
                    PresentedFingerprint = hostKeyState.Fingerprint,
                    PresentedHostKeyAlgorithm = hostKeyState.Algorithm,
                    RequiresTrust = true
                };
            }
        }, cancellationToken);
    }

    public static Task<ServerSnapshot> GetSnapshotAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command = """
printf 'HOST='; hostname 2>/dev/null || echo unknown
printf 'OS='; ( . /etc/os-release 2>/dev/null && printf '%s\n' "$PRETTY_NAME" ) || uname -s
printf 'KERNEL='; uname -r 2>/dev/null || echo unknown
printf 'UPTIME='; uptime -p 2>/dev/null || uptime
printf 'LOAD='; awk '{print $1}' /proc/loadavg 2>/dev/null || echo n/a
printf 'MEM='; awk '/MemTotal/{t=$2}/MemAvailable/{a=$2}END{if(t>0) printf "%d,%d\n",(t-a)/1024,t/1024; else print "0,0"}' /proc/meminfo 2>/dev/null || echo 0,0
printf 'DISK='; df -Pk / 2>/dev/null | awk 'NR==2{printf "%.2f,%.2f\n", $3/1048576, $2/1048576}' || echo 0,0
printf 'CPU='; (top -bn1 2>/dev/null | awk '/Cpu\(s\)/{printf "%.1f\n", 100-$8; found=1; exit} END{if(!found) print "0"}')
printf 'DOCKER='; docker --version 2>/dev/null || echo "Not detected"
""";

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateVerifiedClient(profile, secret);
            client.Connect();
            using var result = client.RunCommand(command);
            if (result.ExitStatus != 0 && string.IsNullOrWhiteSpace(result.Result))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.Error) ? "Server health command failed." : result.Error.Trim());
            }

            return ParseSnapshot(result.Result);
        }, cancellationToken);
    }

    public static Task<IReadOnlyList<ServiceStatus>> GetRunningServicesAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command =
            "systemctl list-units --type=service --state=running --no-legend --no-pager 2>/dev/null | head -n 30";

        return Task.Run<IReadOnlyList<ServiceStatus>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateVerifiedClient(profile, secret);
            client.Connect();
            using var result = client.RunCommand(command);

            var services = new List<ServiceStatus>();
            foreach (var raw in result.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4)
                {
                    continue;
                }

                services.Add(new ServiceStatus
                {
                    Name = parts[0],
                    State = $"{parts[2]}/{parts[3]}",
                    Description = parts.Length > 4 ? string.Join(' ', parts.Skip(4)) : string.Empty
                });
            }

            return services;
        }, cancellationToken);
    }

    public static Task<IReadOnlyList<ServiceStatus>> GetServicesAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command =
            "systemctl list-units --type=service --all --no-legend --no-pager 2>/dev/null | head -n 100";

        return Task.Run<IReadOnlyList<ServiceStatus>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateVerifiedClient(profile, secret);
            client.Connect();
            using var result = client.RunCommand(command);

            if (result.ExitStatus != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.Error)
                        ? "Unable to read system services."
                        : result.Error.Trim());
            }

            return ParseServices(result.Result);
        }, cancellationToken);
    }

    public static Task<string> ServiceActionAsync(
        ServerProfile profile,
        string? secret,
        string serviceName,
        string action,
        CancellationToken cancellationToken = default)
    {
        var normalizedAction = action.ToLowerInvariant();
        if (normalizedAction is not ("start" or "stop" or "restart"))
        {
            throw new ArgumentOutOfRangeException(nameof(action), "Unsupported service action.");
        }

        ValidateServiceName(serviceName);

        return ExecuteCheckedAsync(
            profile,
            secret,
            $"sudo -n systemctl {normalizedAction} {serviceName}",
            cancellationToken);
    }

    public static Task<IReadOnlyList<DockerContainerStatus>> GetDockerContainersAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command =
            "docker ps -a --format '{{.ID}}\\t{{.Names}}\\t{{.Image}}\\t{{.State}}\\t{{.Status}}'";

        return Task.Run<IReadOnlyList<DockerContainerStatus>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateVerifiedClient(profile, secret);
            client.Connect();
            using var result = client.RunCommand(command);

            if (result.ExitStatus != 0)
            {
                var error = string.IsNullOrWhiteSpace(result.Error)
                    ? "Docker is unavailable or the current SSH user cannot access it."
                    : result.Error.Trim();
                throw new InvalidOperationException(error);
            }

            var containers = new List<DockerContainerStatus>();
            foreach (var raw in result.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = raw.TrimEnd('\r').Split('\t');
                if (parts.Length < 5)
                {
                    continue;
                }

                containers.Add(new DockerContainerStatus
                {
                    Id = parts[0],
                    Name = parts[1],
                    Image = parts[2],
                    State = parts[3],
                    Status = parts[4]
                });
            }

            return containers;
        }, cancellationToken);
    }

    public static Task<string> DockerActionAsync(
        ServerProfile profile,
        string? secret,
        string container,
        string action,
        CancellationToken cancellationToken = default)
    {
        var normalizedAction = action.ToLowerInvariant();
        if (normalizedAction is not ("start" or "stop" or "restart"))
        {
            throw new ArgumentOutOfRangeException(nameof(action), "Unsupported Docker action.");
        }

        ValidateDockerIdentifier(container);

        return ExecuteCheckedAsync(
            profile,
            secret,
            $"docker {normalizedAction} {container}",
            cancellationToken);
    }

    public static Task<string> GetNetworkOverviewAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command = """
printf 'Addresses
---------
'
(hostname -I 2>/dev/null || true)
printf '
Interfaces
----------
'
(ip -brief address 2>/dev/null || ifconfig 2>/dev/null || true)
printf '
Routes
------
'
(ip route 2>/dev/null || route -n 2>/dev/null || true)
printf '
Listening sockets
-----------------
'
(ss -lntup 2>/dev/null | head -n 100 || netstat -lntup 2>/dev/null | head -n 100 || true)
printf '
Firewall
--------
'
if command -v ufw >/dev/null 2>&1; then
  printf 'Backend: UFW
'
  sudo -n ufw status numbered 2>/dev/null || ufw status 2>/dev/null || echo 'UFW status requires elevated privileges.'
elif command -v firewall-cmd >/dev/null 2>&1; then
  printf 'Backend: firewalld
'
  firewall-cmd --state 2>/dev/null || true
  firewall-cmd --list-all 2>/dev/null || echo 'firewalld details require elevated privileges.'
else
  echo 'No supported firewall backend detected.'
fi
""";

        return RunCommandAsync(profile, secret, command, cancellationToken);
    }

    public static Task<string> AllowFirewallPortAsync(
        ServerProfile profile,
        string? secret,
        int port,
        string protocol,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        var normalizedProtocol = protocol.Trim().ToLowerInvariant();
        if (normalizedProtocol is not ("tcp" or "udp"))
        {
            throw new ArgumentOutOfRangeException(nameof(protocol), "Protocol must be TCP or UDP.");
        }

        var command = $"""
if command -v ufw >/dev/null 2>&1; then
  sudo -n ufw allow {port}/{normalizedProtocol}
elif command -v firewall-cmd >/dev/null 2>&1; then
  sudo -n firewall-cmd --permanent --add-port={port}/{normalizedProtocol}
  sudo -n firewall-cmd --reload
else
  echo 'No supported firewall backend detected.' >&2
  exit 127
fi
""";

        return ExecuteCheckedAsync(profile, secret, command, cancellationToken);
    }

    public static Task<IReadOnlyList<ScheduledTaskStatus>> GetScheduledTasksAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command = """
for timer in /etc/systemd/system/ghost-server-*.timer; do
  [ -e "$timer" ] || continue
  unit=$(basename "$timer")
  name=${unit#ghost-server-}
  name=${name%.timer}
  schedule=$(awk -F= '/^OnCalendar=/{print $2; exit}' "$timer")
  next=$(systemctl show "$unit" -p NextElapseUSecRealtime --value 2>/dev/null || true)
  state=$(systemctl is-active "$unit" 2>/dev/null || true)
  enabled=$(systemctl is-enabled "$unit" 2>/dev/null || true)
  printf '%s\t%s\t%s\t%s\t%s\n' "$name" "$unit" "$schedule" "$next" "$state|$enabled"
done
""";

        return Task.Run<IReadOnlyList<ScheduledTaskStatus>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateVerifiedClient(profile, secret);
            client.Connect();
            using var result = client.RunCommand(command);

            if (result.ExitStatus != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.Error)
                        ? $"Remote command failed with exit code {result.ExitStatus}."
                        : result.Error.Trim());
            }

            return ParseScheduledTasks(result.Result ?? string.Empty);
        }, cancellationToken);
    }

    public static Task<string> GetUserCrontabAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command = """
echo "Current user crontab"
echo "--------------------"
crontab -l 2>/dev/null || echo "(no crontab entries)"
""";

        return RunCommandAsync(profile, secret, command, cancellationToken);
    }

    public static Task<string> CreateScheduledTaskAsync(
        ServerProfile profile,
        string? secret,
        string name,
        string schedule,
        string userCommand,
        CancellationToken cancellationToken = default)
    {
        var slug = NormalizeScheduledTaskName(name);
        var onCalendar = schedule switch
        {
            "Hourly" => "hourly",
            "Daily" => "daily",
            "Weekly" => "weekly",
            _ => throw new ArgumentOutOfRangeException(
                nameof(schedule),
                "Schedule must be Hourly, Daily or Weekly.")
        };

        if (string.IsNullOrWhiteSpace(userCommand))
        {
            throw new ArgumentException(
                "Scheduled command is required.",
                nameof(userCommand));
        }

        if (userCommand.Length > 4096)
        {
            throw new ArgumentException(
                "Scheduled command is too long.",
                nameof(userCommand));
        }

        var script = "#!/usr/bin/env bash\nset -euo pipefail\n" + userCommand.Trim() + "\n";
        var encodedScript = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));

        var command = string.Join(
            "\n",
            new[]
            {
                "set -eu",
                "if ! sudo -n true >/dev/null 2>&1; then",
                "  echo \"Task creation blocked: passwordless sudo is required.\" >&2",
                "  exit 40",
                "fi",
                string.Empty,
                $"slug='{slug}'",
                "script=\"/usr/local/lib/ghost-server/tasks/ghost-server-$slug.sh\"",
                "service=\"/etc/systemd/system/ghost-server-$slug.service\"",
                "timer=\"/etc/systemd/system/ghost-server-$slug.timer\"",
                string.Empty,
                "sudo -n install -d -m 700 /usr/local/lib/ghost-server/tasks",
                $"printf '%s' '{encodedScript}' | base64 -d | sudo -n tee \"$script\" >/dev/null",
                "sudo -n chmod 700 \"$script\"",
                "sudo -n chown root:root \"$script\"",
                string.Empty,
                "sudo -n tee \"$service\" >/dev/null <<EOF",
                "[Unit]",
                $"Description=Ghost Server scheduled task: {slug}",
                "After=network-online.target",
                "Wants=network-online.target",
                string.Empty,
                "[Service]",
                "Type=oneshot",
                "ExecStart=$script",
                "EOF",
                string.Empty,
                "sudo -n tee \"$timer\" >/dev/null <<EOF",
                "[Unit]",
                $"Description=Ghost Server timer: {slug}",
                string.Empty,
                "[Timer]",
                $"OnCalendar={onCalendar}",
                "Persistent=true",
                "AccuracySec=1m",
                $"Unit=ghost-server-{slug}.service",
                string.Empty,
                "[Install]",
                "WantedBy=timers.target",
                "EOF",
                string.Empty,
                "sudo -n systemctl daemon-reload",
                "sudo -n systemctl enable --now \"ghost-server-$slug.timer\"",
                $"echo \"Created Ghost Server task '$slug' ({schedule}).\""
            });

        return ExecuteCheckedAsync(
            profile,
            secret,
            command,
            cancellationToken);
    }

    public static Task<string> DeleteScheduledTaskAsync(
        ServerProfile profile,
        string? secret,
        string name,
        CancellationToken cancellationToken = default)
    {
        var slug = NormalizeScheduledTaskName(name);

        var command = string.Join(
            "\n",
            new[]
            {
                "set -eu",
                "if ! sudo -n true >/dev/null 2>&1; then",
                "  echo \"Task deletion blocked: passwordless sudo is required.\" >&2",
                "  exit 41",
                "fi",
                string.Empty,
                $"slug='{slug}'",
                "timer=\"/etc/systemd/system/ghost-server-$slug.timer\"",
                "service=\"/etc/systemd/system/ghost-server-$slug.service\"",
                "script=\"/usr/local/lib/ghost-server/tasks/ghost-server-$slug.sh\"",
                string.Empty,
                "sudo -n systemctl disable --now \"ghost-server-$slug.timer\" 2>/dev/null || true",
                "sudo -n rm -f \"$timer\" \"$service\" \"$script\"",
                "sudo -n systemctl daemon-reload",
                "sudo -n systemctl reset-failed \"ghost-server-$slug.service\" 2>/dev/null || true",
                "echo \"Deleted Ghost Server task '$slug'.\""
            });

        return ExecuteCheckedAsync(
            profile,
            secret,
            command,
            cancellationToken);
    }

    public static Task<string> GetSafeUpdatePreviewAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command = """
echo "Ghost Server Safe Update preview"
echo "--------------------------------"
printf "Host: "; hostname 2>/dev/null || echo unknown
printf "Kernel: "; uname -r 2>/dev/null || echo unknown
printf "Root free space: "; df -hP / 2>/dev/null | awk 'NR==2 {print $4 " free of " $2}' || echo unknown
printf "Reboot already required: "; if [ -f /var/run/reboot-required ]; then echo yes; else echo no; fi
printf "Failed systemd units: "; systemctl --failed --no-legend --no-pager 2>/dev/null | wc -l || echo unknown
echo

if command -v apt >/dev/null 2>&1; then
  echo "Package manager: APT"
  echo
  apt list --upgradable 2>/dev/null | sed '1d' | head -n 200
elif command -v dnf >/dev/null 2>&1; then
  echo "Package manager: DNF"
  echo
  dnf -q check-update 2>/dev/null || true
elif command -v yum >/dev/null 2>&1; then
  echo "Package manager: YUM"
  echo
  yum -q check-update 2>/dev/null || true
elif command -v zypper >/dev/null 2>&1; then
  echo "Package manager: Zypper"
  echo
  zypper --non-interactive list-updates 2>/dev/null || true
elif command -v pacman >/dev/null 2>&1; then
  echo "Package manager: pacman"
  echo
  if command -v checkupdates >/dev/null 2>&1; then
    checkupdates 2>/dev/null || true
  else
    pacman -Qu 2>/dev/null || true
  fi
else
  echo "Package manager: unsupported"
fi
""";

        return RunCommandAsync(profile, secret, command, cancellationToken);
    }

    public static Task<string> RunSafeUpdateAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command = """
set -eu

available_kb=$(df -Pk / 2>/dev/null | awk 'NR==2 {print $4}')
if [ -z "$available_kb" ] || [ "$available_kb" -lt 1048576 ]; then
  echo "Safe Update blocked: at least 1 GiB of free root-disk space is required." >&2
  exit 20
fi

if ! sudo -n true >/dev/null 2>&1; then
  echo "Safe Update blocked: passwordless sudo is required for the connected account." >&2
  exit 21
fi

echo "Ghost Server Safe Update"
echo "========================"
printf "Started: "; date -Iseconds 2>/dev/null || date
printf "Host: "; hostname 2>/dev/null || echo unknown
printf "Kernel before: "; uname -r 2>/dev/null || echo unknown
echo

if command -v apt-get >/dev/null 2>&1; then
  echo "[APT] Refreshing package metadata..."
  sudo -n apt-get update
  echo "[APT] Installing regular upgrades..."
  sudo -n env DEBIAN_FRONTEND=noninteractive apt-get -y -o Dpkg::Options::=--force-confold upgrade
elif command -v dnf >/dev/null 2>&1; then
  echo "[DNF] Installing upgrades..."
  sudo -n dnf -y upgrade
elif command -v yum >/dev/null 2>&1; then
  echo "[YUM] Installing upgrades..."
  sudo -n yum -y update
elif command -v zypper >/dev/null 2>&1; then
  echo "[Zypper] Installing upgrades..."
  sudo -n zypper --non-interactive update
elif command -v pacman >/dev/null 2>&1; then
  echo "[pacman] Installing upgrades..."
  sudo -n pacman -Syu --noconfirm
else
  echo "Safe Update blocked: no supported package manager was detected." >&2
  exit 22
fi

echo
echo "Post-update summary"
echo "-------------------"
printf "Finished: "; date -Iseconds 2>/dev/null || date
printf "Kernel after: "; uname -r 2>/dev/null || echo unknown
printf "Reboot required: "; if [ -f /var/run/reboot-required ]; then echo yes; else echo no; fi
printf "Failed systemd units: "; systemctl --failed --no-legend --no-pager 2>/dev/null | wc -l || echo unknown
printf "Root free space: "; df -hP / 2>/dev/null | awk 'NR==2 {print $4 " free of " $2}' || echo unknown
""";

        return ExecuteLongRunningCheckedAsync(
            profile,
            secret,
            command,
            cancellationToken);
    }

    public static Task<string> GetSafeUpdateHealthAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command = """
echo "Ghost Server post-update health check"
echo "-------------------------------------"
printf "Host: "; hostname 2>/dev/null || echo unknown
printf "Kernel: "; uname -r 2>/dev/null || echo unknown
printf "Uptime: "; uptime -p 2>/dev/null || uptime
printf "Load: "; awk '{print $1 ", " $2 ", " $3}' /proc/loadavg 2>/dev/null || echo unknown
printf "Root disk: "; df -hP / 2>/dev/null | awk 'NR==2 {print $3 " used / " $2 " total (" $5 ")"}' || echo unknown
printf "Reboot required: "; if [ -f /var/run/reboot-required ]; then echo yes; else echo no; fi
echo
echo "Failed systemd units"
echo "--------------------"
systemctl --failed --no-legend --no-pager 2>/dev/null | head -n 30 || true
echo
echo "Pending updates after run"
echo "-------------------------"
if command -v apt >/dev/null 2>&1; then
  apt list --upgradable 2>/dev/null | sed '1d' | head -n 100
elif command -v dnf >/dev/null 2>&1; then
  dnf -q check-update 2>/dev/null || true
elif command -v yum >/dev/null 2>&1; then
  yum -q check-update 2>/dev/null || true
elif command -v zypper >/dev/null 2>&1; then
  zypper --non-interactive list-updates 2>/dev/null || true
elif command -v pacman >/dev/null 2>&1; then
  pacman -Qu 2>/dev/null || true
fi
""";

        return RunCommandAsync(profile, secret, command, cancellationToken);
    }

    public static Task UploadFileToPathAsync(
        ServerProfile profile,
        string? secret,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(localPath))
            {
                throw new FileNotFoundException("Local file does not exist.", localPath);
            }

            using var client = CreateVerifiedSftpClient(profile, secret);
            client.Connect();

            using var stream = File.OpenRead(localPath);
            client.UploadFile(stream, NormalizeRemotePath(remotePath), true);
        }, cancellationToken);
    }

    public static Task<string> RestoreConfigurationSnapshotAsync(
        ServerProfile profile,
        string? secret,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                remotePath,
                @"^/tmp/ghost-server-restore-[a-f0-9]{32}[.]tar[.]gz$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new ArgumentException(
                "Invalid temporary restore path.",
                nameof(remotePath));
        }

        var command = $$"""
set -eu
archive='{{remotePath}}'

if [ ! -f "$archive" ]; then
  echo "Restore archive is missing." >&2
  exit 30
fi

if ! sudo -n true >/dev/null 2>&1; then
  echo "Restore blocked: passwordless sudo is required." >&2
  exit 31
fi

if ! tar -tzf "$archive" >/dev/null 2>&1; then
  echo "Restore blocked: archive is not a valid gzip tar snapshot." >&2
  exit 32
fi

if ! tar -tzf "$archive" | awk '
  substr($0, 1, 1) == "/" || $0 ~ /(^|[/])[.][.]([/]|$)/ { exit 1 }
  /^etc[/]ssh([/]|$)/ { next }
  /^etc[/]nginx([/]|$)/ { next }
  /^etc[/]apache2([/]|$)/ { next }
  /^etc[/]systemd[/]system([/]|$)/ { next }
  /^etc[/]docker([/]|$)/ { next }
  /^etc[/]fail2ban([/]|$)/ { next }
  /^etc[/]ufw([/]|$)/ { next }
  { exit 1 }
'; then
  echo "Restore blocked: archive contains paths outside the Ghost Server configuration allowlist." >&2
  exit 33
fi

if ! tar -tvzf "$archive" | awk '
  { type = substr($1, 1, 1); if (type != "-" && type != "d") exit 1 }
'; then
  echo "Restore blocked: links or special filesystem entries are not accepted." >&2
  exit 34
fi

echo "Validated snapshot contents."
sudo -n tar -xzf "$archive" -C /
sudo -n systemctl daemon-reload 2>/dev/null || true

echo "Configuration snapshot restored."
echo "No service restart or reboot was performed automatically."
""";

        return ExecuteLongRunningCheckedAsync(
            profile,
            secret,
            command,
            cancellationToken);
    }

    public static Task<string> CreateConfigurationSnapshotAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        var remotePath =
            $"/tmp/ghost-server-config-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.tar.gz";

        var command = $"""
set -eu
archive='{remotePath}'
paths=''
for p in /etc/ssh /etc/nginx /etc/apache2 /etc/systemd/system /etc/docker /etc/fail2ban /etc/ufw; do
  if [ -e "$p" ]; then
    paths="$paths $p"
  fi
done

if [ -z "$paths" ]; then
  echo 'No supported configuration directories were found.' >&2
  exit 2
fi

sudo -n tar -czf "$archive" --ignore-failed-read $paths
sudo -n chown "$(id -u):$(id -g)" "$archive"
printf '%s' "$archive"
""";

        return ExecuteCheckedAsync(profile, secret, command, cancellationToken);
    }

    public static Task DeleteRemoteFileAsync(
        ServerProfile profile,
        string? secret,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateVerifiedSftpClient(profile, secret);
            client.Connect();

            var target = NormalizeRemotePath(remotePath);
            if (client.Exists(target))
            {
                client.DeleteFile(target);
            }
        }, cancellationToken);
    }

    public static Task<IReadOnlyList<ProcessStatus>> GetProcessesAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command =
            "ps -eo pid=,user=,pcpu=,pmem=,etime=,comm= --sort=-pcpu 2>/dev/null | head -n 100";

        return Task.Run<IReadOnlyList<ProcessStatus>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateVerifiedClient(profile, secret);
            client.Connect();
            using var result = client.RunCommand(command);

            if (result.ExitStatus != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.Error)
                        ? "Unable to read process list."
                        : result.Error.Trim());
            }

            var processes = new List<ProcessStatus>();
            foreach (var raw in result.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = raw.Trim()
                    .Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length != 6 ||
                    !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu) ||
                    !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var memory))
                {
                    continue;
                }

                processes.Add(new ProcessStatus
                {
                    Pid = pid,
                    User = parts[1],
                    CpuPercent = cpu,
                    MemoryPercent = memory,
                    Elapsed = parts[4],
                    Command = parts[5]
                });
            }

            return processes;
        }, cancellationToken);
    }

    public static Task<string> GetSystemOverviewAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command = """
echo "Memory"
echo "------"
free -h 2>/dev/null || true
echo
echo "Filesystems"
echo "-----------"
df -hPT 2>/dev/null || true
echo
echo "Block devices"
echo "-------------"
lsblk -o NAME,TYPE,FSTYPE,SIZE,FSAVAIL,FSUSE%,MOUNTPOINTS 2>/dev/null || true
echo
echo "Logged-in users"
echo "---------------"
who 2>/dev/null || true
echo
echo "Load"
echo "----"
uptime 2>/dev/null || true
""";

        return RunCommandAsync(profile, secret, command, cancellationToken);
    }

    public static Task<string> TerminateProcessAsync(
        ServerProfile profile,
        string? secret,
        int pid,
        CancellationToken cancellationToken = default)
    {
        if (pid <= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pid),
                "PID must be greater than 1.");
        }

        var command = $"""
pid={pid}
if ! kill -0 "$pid" 2>/dev/null && ! sudo -n kill -0 "$pid" 2>/dev/null; then
  echo "Process $pid no longer exists." >&2
  exit 44
fi

if kill -TERM "$pid" 2>/dev/null; then
  echo "SIGTERM sent to process $pid."
elif sudo -n kill -TERM "$pid"; then
  echo "SIGTERM sent to process $pid with sudo."
else
  echo "Unable to terminate process $pid." >&2
  exit 45
fi
""";

        return ExecuteCheckedAsync(profile, secret, command, cancellationToken);
    }

    public static async Task<IReadOnlyList<DatabaseEngineStatus>> GetDatabaseEnginesAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command = """
safe() {
  printf '%s' "$1" | tr '\t\r\n' '   '
}

emit_meta() {
  printf 'META\t%s\t%s\t%s\t%s\n' "$(safe "$1")" "$(safe "$2")" "$(safe "$3")" "$(safe "$4")"
}

emit_db() {
  printf 'DB\t%s\t%s\n' "$(safe "$1")" "$(safe "$2")"
}

if command -v psql >/dev/null 2>&1; then
  pg_version="$(psql --version 2>/dev/null | head -n 1)"
  pg_status="$(systemctl is-active postgresql 2>/dev/null || true)"
  [ -n "$pg_status" ] || pg_status="unknown"
  pg_access="no non-interactive access"
  pg_sql='SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname;'

  pg_dbs="$(psql -X -A -t -c "$pg_sql" 2>/dev/null)"
  pg_rc=$?

  if [ "$pg_rc" -eq 0 ]; then
    pg_access="current user"
  else
    pg_dbs="$(sudo -n -u postgres psql -X -A -t -c "$pg_sql" 2>/dev/null)"
    pg_rc=$?
    if [ "$pg_rc" -eq 0 ]; then
      pg_access="postgres socket via sudo"
    fi
  fi

  emit_meta "PostgreSQL" "$pg_version" "$pg_status" "$pg_access"

  if [ "$pg_rc" -eq 0 ] && [ -n "$pg_dbs" ]; then
    printf '%s\n' "$pg_dbs" | while IFS= read -r db; do
      [ -n "$db" ] && emit_db "PostgreSQL" "$db"
    done
  fi
fi

my_client=""
if command -v mariadb >/dev/null 2>&1; then
  my_client="$(command -v mariadb)"
elif command -v mysql >/dev/null 2>&1; then
  my_client="$(command -v mysql)"
fi

if [ -n "$my_client" ]; then
  my_version="$("$my_client" --version 2>/dev/null | head -n 1)"
  case "$my_version" in
    *MariaDB*|*mariadb*) my_engine="MariaDB" ;;
    *) my_engine="MySQL" ;;
  esac

  my_status="$(systemctl is-active mariadb 2>/dev/null || true)"
  if [ -z "$my_status" ] || [ "$my_status" = "unknown" ]; then
    my_status="$(systemctl is-active mysql 2>/dev/null || true)"
  fi
  [ -n "$my_status" ] || my_status="unknown"

  my_access="no non-interactive access"
  my_dbs="$("$my_client" --batch --skip-column-names --connect-timeout=5 -e 'SHOW DATABASES;' 2>/dev/null)"
  my_rc=$?

  if [ "$my_rc" -eq 0 ]; then
    my_access="configured/socket auth"
  else
    my_dbs="$(sudo -n "$my_client" --batch --skip-column-names --connect-timeout=5 -e 'SHOW DATABASES;' 2>/dev/null)"
    my_rc=$?
    if [ "$my_rc" -eq 0 ]; then
      my_access="local root socket via sudo"
    fi
  fi

  emit_meta "$my_engine" "$my_version" "$my_status" "$my_access"

  if [ "$my_rc" -eq 0 ] && [ -n "$my_dbs" ]; then
    printf '%s\n' "$my_dbs" | while IFS= read -r db; do
      [ -n "$db" ] && emit_db "$my_engine" "$db"
    done
  fi
fi

if command -v sqlite3 >/dev/null 2>&1; then
  sqlite_version="$(sqlite3 --version 2>/dev/null | awk '{print $1}')"
  emit_meta "SQLite" "$sqlite_version" "client only" "filesystem databases are not enumerated"
fi
""";

        var output = await RunCommandAsync(
            profile,
            secret,
            command,
            cancellationToken);

        var engines = new List<DatabaseEngineStatus>();
        var byName = new Dictionary<string, DatabaseEngineStatus>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in output.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = rawLine.Split('\t');
            if (parts.Length >= 5 &&
                string.Equals(parts[0], "META", StringComparison.Ordinal))
            {
                var engine = new DatabaseEngineStatus
                {
                    Engine = parts[1],
                    Version = parts[2],
                    ServiceStatus = parts[3],
                    Access = parts[4]
                };

                engines.Add(engine);
                byName[engine.Engine] = engine;
                continue;
            }

            if (parts.Length >= 3 &&
                string.Equals(parts[0], "DB", StringComparison.Ordinal) &&
                byName.TryGetValue(parts[1], out var target) &&
                !string.IsNullOrWhiteSpace(parts[2]))
            {
                target.Databases.Add(parts[2]);
            }
        }

        return engines;
    }

    public static Task<string> GetRecentLogsAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command =
            "journalctl -n 200 --no-pager -o short-iso 2>/dev/null || dmesg --ctime 2>/dev/null | tail -n 200";

        return RunCommandAsync(profile, secret, command, cancellationToken);
    }

    public static Task<IReadOnlyList<RemoteFileItem>> GetRemoteFilesAsync(
        ServerProfile profile,
        string? secret,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<RemoteFileItem>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateVerifiedSftpClient(profile, secret);
            client.Connect();

            var target = NormalizeRemotePath(remotePath);
            return client.ListDirectory(target)
                .Where(item => item.Name is not "." and not "..")
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new RemoteFileItem
                {
                    Name = item.Name,
                    FullPath = item.FullName,
                    IsDirectory = item.IsDirectory,
                    SizeBytes = item.IsDirectory ? 0 : item.Length,
                    LastWriteTime = item.LastWriteTime
                })
                .ToArray();
        }, cancellationToken);
    }

    public static Task UploadFileAsync(
        ServerProfile profile,
        string? secret,
        string localPath,
        string remoteDirectory,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(localPath))
            {
                throw new FileNotFoundException("Local file does not exist.", localPath);
            }

            using var client = CreateVerifiedSftpClient(profile, secret);
            client.Connect();

            var directory = NormalizeRemotePath(remoteDirectory).TrimEnd('/');
            var remotePath = directory.Length == 0
                ? "/" + Path.GetFileName(localPath)
                : directory + "/" + Path.GetFileName(localPath);

            using var stream = File.OpenRead(localPath);
            client.UploadFile(stream, remotePath, true);
        }, cancellationToken);
    }

    public static Task DownloadFileAsync(
        ServerProfile profile,
        string? secret,
        string remotePath,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateVerifiedSftpClient(profile, secret);
            client.Connect();

            using var stream = File.Create(localPath);
            client.DownloadFile(NormalizeRemotePath(remotePath), stream);
        }, cancellationToken);
    }

    public static Task<string> GetServiceLogsAsync(
        ServerProfile profile,
        string? secret,
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        ValidateServiceName(serviceName);
        return RunCommandAsync(
            profile,
            secret,
            $"journalctl -u {serviceName} -n 200 --no-pager -o short-iso 2>/dev/null",
            cancellationToken);
    }

    public static Task<string> GetDockerLogsAsync(
        ServerProfile profile,
        string? secret,
        string container,
        CancellationToken cancellationToken = default)
    {
        ValidateDockerIdentifier(container);
        return RunCommandAsync(
            profile,
            secret,
            $"docker logs --tail 200 {container} 2>&1",
            cancellationToken);
    }

    public static Task<string> RunCommandAsync(
        ServerProfile profile,
        string? secret,
        string command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Task.FromResult(string.Empty);
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateVerifiedClient(profile, secret);
            client.Connect();
            using var result = client.RunCommand(command);
            var output = result.Result ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                output += (output.EndsWith('\n') || output.Length == 0 ? string.Empty : Environment.NewLine)
                          + result.Error;
            }

            return output.TrimEnd();
        }, cancellationToken);
    }

    public static Task<string> RunSecurityScanAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command = """
echo "Ghost Server security scan"
echo "--------------------------"
printf "Root SSH login: "; (sshd -T 2>/dev/null | awk '/^permitrootlogin /{print $2; exit}') || echo unknown
printf "SSH password authentication: "; (sshd -T 2>/dev/null | awk '/^passwordauthentication /{print $2; exit}') || echo unknown
printf "Firewall: "; if command -v ufw >/dev/null 2>&1; then ufw status 2>/dev/null | head -n1; elif command -v firewall-cmd >/dev/null 2>&1; then firewall-cmd --state 2>/dev/null; else echo "not detected"; fi
printf "Pending package updates: "; if command -v apt >/dev/null 2>&1; then apt list --upgradable 2>/dev/null | tail -n +2 | wc -l; elif command -v dnf >/dev/null 2>&1; then dnf -q check-update 2>/dev/null | grep -cE '^[[:alnum:]_.+-]+[.]'; else echo "unknown"; fi
printf "Docker socket permissions: "; if [ -S /var/run/docker.sock ]; then stat -c '%a %U:%G' /var/run/docker.sock 2>/dev/null || echo unknown; else echo "not present"; fi
""";

        return RunCommandAsync(profile, secret, command, cancellationToken);
    }

    private static string NormalizeScheduledTaskName(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();

        if (!System.Text.RegularExpressions.Regex.IsMatch(
                normalized,
                @"^[a-z0-9][a-z0-9-]{0,39}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new ArgumentException(
                "Task name must use lowercase letters, numbers or hyphens and be at most 40 characters.",
                nameof(value));
        }

        return normalized;
    }

    private static List<ScheduledTaskStatus> ParseScheduledTasks(string output)
    {
        var result = new List<ScheduledTaskStatus>();

        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = raw.TrimEnd('\r').Split('\t');
            if (columns.Length != 5)
            {
                continue;
            }

            var stateParts = columns[4].Split('|', 2);
            result.Add(new ScheduledTaskStatus
            {
                Name = columns[0],
                TimerUnit = columns[1],
                Schedule = columns[2],
                NextRun = string.IsNullOrWhiteSpace(columns[3]) ? "—" : columns[3],
                State = stateParts.Length > 0 ? stateParts[0] : "unknown",
                Enabled = stateParts.Length > 1 &&
                          stateParts[1].StartsWith("enabled", StringComparison.OrdinalIgnoreCase)
            });
        }

        return result;
    }

    private static void ValidateServiceName(string serviceName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                serviceName,
                @"^[A-Za-z0-9][A-Za-z0-9@_.:-]*\.service$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Invalid systemd service identifier.", nameof(serviceName));
        }
    }

    private static void ValidateDockerIdentifier(string container)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                container,
                @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Invalid Docker container identifier.", nameof(container));
        }
    }

    private static string NormalizeRemotePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized;
    }

    private static SftpClient CreateVerifiedSftpClient(ServerProfile profile, string? secret)
    {
        if (string.IsNullOrWhiteSpace(profile.HostKeyFingerprint))
        {
            throw new SecurityException("Host key has not been approved for this server.");
        }

        var client = new SftpClient(CreateConnectionInfo(profile, secret))
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            OperationTimeout = TimeSpan.FromSeconds(45)
        };

        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = CryptographicEquals(profile.HostKeyFingerprint, e.FingerPrintSHA256);
        };

        return client;
    }

    private static SshClient CreateVerifiedClient(ServerProfile profile, string? secret)
    {
        if (string.IsNullOrWhiteSpace(profile.HostKeyFingerprint))
        {
            throw new SecurityException("Host key has not been approved for this server.");
        }

        return CreateClient(profile, secret, out _);
    }

    private static SshClient CreateClient(
        ServerProfile profile,
        string? secret,
        out HostKeyState hostKeyState)
    {
        var state = new HostKeyState();
        var client = new SshClient(CreateConnectionInfo(profile, secret))
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        };

        client.HostKeyReceived += (_, e) =>
        {
            state.Fingerprint = e.FingerPrintSHA256;
            state.Algorithm = e.HostKeyName;
            e.CanTrust = !string.IsNullOrWhiteSpace(profile.HostKeyFingerprint)
                         && CryptographicEquals(profile.HostKeyFingerprint, e.FingerPrintSHA256);
        };

        hostKeyState = state;
        return client;
    }

    private static ConnectionInfo CreateConnectionInfo(
        ServerProfile profile,
        string? secret)
    {
        var authentication = CreateAuthenticationMethod(profile, secret);

        return new ConnectionInfo(
            profile.Host,
            profile.Port,
            profile.Username,
            authentication)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    private static AuthenticationMethod CreateAuthenticationMethod(
        ServerProfile profile,
        string? secret)
    {
        if (!string.Equals(profile.Authentication, "PrivateKey", StringComparison.OrdinalIgnoreCase))
        {
            return new PasswordAuthenticationMethod(
                profile.Username,
                secret ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            throw new InvalidOperationException("Private key path is required.");
        }

        if (!File.Exists(profile.PrivateKeyPath))
        {
            throw new FileNotFoundException(
                "Private key file was not found.",
                profile.PrivateKeyPath);
        }

        var key = string.IsNullOrEmpty(secret)
            ? new PrivateKeyFile(profile.PrivateKeyPath)
            : new PrivateKeyFile(profile.PrivateKeyPath, secret);

        return new PrivateKeyAuthenticationMethod(profile.Username, key);
    }

    private static bool CryptographicEquals(string expected, string presented)
    {
        var left = System.Text.Encoding.UTF8.GetBytes(expected);
        var right = System.Text.Encoding.UTF8.GetBytes(presented);
        return left.Length == right.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static ServerSnapshot ParseSnapshot(string output)
    {
        var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.OrdinalIgnoreCase);

        var mem = ParsePair(values.GetValueOrDefault("MEM"));
        var disk = ParseDoublePair(values.GetValueOrDefault("DISK"));

        return new ServerSnapshot
        {
            Hostname = values.GetValueOrDefault("HOST", "—"),
            OperatingSystem = values.GetValueOrDefault("OS", "—"),
            Kernel = values.GetValueOrDefault("KERNEL", "—"),
            Uptime = values.GetValueOrDefault("UPTIME", "—"),
            Load = values.GetValueOrDefault("LOAD", "—"),
            CpuPercent = ParseDouble(values.GetValueOrDefault("CPU")),
            MemoryUsedMb = mem.Item1,
            MemoryTotalMb = mem.Item2,
            DiskUsedGb = disk.Item1,
            DiskTotalGb = disk.Item2,
            Docker = values.GetValueOrDefault("DOCKER", "Not detected")
        };
    }

    private static List<ServiceStatus> ParseServices(string output)
    {
        var services = new List<ServiceStatus>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            services.Add(new ServiceStatus
            {
                Name = parts[0],
                State = $"{parts[2]}/{parts[3]}",
                Description = parts.Length > 4 ? string.Join(' ', parts.Skip(4)) : string.Empty
            });
        }

        return services;
    }

    private static Task<string> ExecuteLongRunningCheckedAsync(
        ServerProfile profile,
        string? secret,
        string command,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateVerifiedClient(profile, secret);
            client.Connect();
            using var result = client.CreateCommand(command);
            result.CommandTimeout = TimeSpan.FromMinutes(30);

            var output = result.Execute()?.TrimEnd() ?? string.Empty;
            var error = result.Error?.TrimEnd() ?? string.Empty;

            if (result.ExitStatus != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"Remote command failed with exit code {result.ExitStatus}."
                        : error);
            }

            return string.IsNullOrWhiteSpace(output)
                ? "Command completed."
                : output;
        }, cancellationToken);
    }

    private static Task<string> ExecuteCheckedAsync(
        ServerProfile profile,
        string? secret,
        string command,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateVerifiedClient(profile, secret);
            client.Connect();
            using var result = client.RunCommand(command);

            var output = result.Result?.TrimEnd() ?? string.Empty;
            var error = result.Error?.TrimEnd() ?? string.Empty;

            if (result.ExitStatus != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"Remote command failed with exit code {result.ExitStatus}."
                        : error);
            }

            return string.IsNullOrWhiteSpace(output) ? "Command completed." : output;
        }, cancellationToken);
    }

    private static (long, long) ParsePair(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (0, 0);
        }

        var parts = value.Split(',', 2);
        return parts.Length == 2 &&
               long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) &&
               long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)
            ? (a, b)
            : (0, 0);
    }

    private static (double, double) ParseDoublePair(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (0, 0);
        }

        var parts = value.Split(',', 2);
        return parts.Length == 2
            ? (ParseDouble(parts[0]), ParseDouble(parts[1]))
            : (0, 0);
    }

    private static double ParseDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private sealed class HostKeyState
    {
        public string? Fingerprint { get; set; }
        public string? Algorithm { get; set; }
    }
}
