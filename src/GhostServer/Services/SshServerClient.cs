using System.Globalization;
using System.Security;
using GhostServer.Models;
using Renci.SshNet;

namespace GhostServer.Services;

public sealed class SshServerClient
{
    public Task<ConnectionProbe> ProbeAsync(
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

    public Task<ServerSnapshot> GetSnapshotAsync(
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

    public Task<IReadOnlyList<ServiceStatus>> GetRunningServicesAsync(
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

    public Task<IReadOnlyList<ServiceStatus>> GetServicesAsync(
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

    public Task<string> ServiceActionAsync(
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

        if (!System.Text.RegularExpressions.Regex.IsMatch(
                serviceName,
                @"^[A-Za-z0-9][A-Za-z0-9@_.:-]*\.service$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Invalid systemd service identifier.", nameof(serviceName));
        }

        return ExecuteCheckedAsync(
            profile,
            secret,
            $"sudo -n systemctl {normalizedAction} {serviceName}",
            cancellationToken);
    }

    public Task<IReadOnlyList<DockerContainerStatus>> GetDockerContainersAsync(
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

    public Task<string> DockerActionAsync(
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

        if (!System.Text.RegularExpressions.Regex.IsMatch(
                container,
                @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Invalid Docker container identifier.", nameof(container));
        }

        return ExecuteCheckedAsync(
            profile,
            secret,
            $"docker {normalizedAction} {container}",
            cancellationToken);
    }

    public Task<string> GetRecentLogsAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        const string command =
            "journalctl -n 200 --no-pager -o short-iso 2>/dev/null || dmesg --ctime 2>/dev/null | tail -n 200";

        return RunCommandAsync(profile, secret, command, cancellationToken);
    }

    public Task<string> RunCommandAsync(
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

    public Task<string> RunSecurityScanAsync(
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
        AuthenticationMethod authentication;

        if (string.Equals(profile.Authentication, "PrivateKey", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
            {
                throw new InvalidOperationException("Private key path is required.");
            }

            var key = string.IsNullOrEmpty(secret)
                ? new PrivateKeyFile(profile.PrivateKeyPath)
                : new PrivateKeyFile(profile.PrivateKeyPath, secret);

            authentication = new PrivateKeyAuthenticationMethod(profile.Username, key);
        }
        else
        {
            authentication = new PasswordAuthenticationMethod(profile.Username, secret ?? string.Empty);
        }

        var connection = new ConnectionInfo(
            profile.Host,
            profile.Port,
            profile.Username,
            authentication)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        var state = new HostKeyState();
        var client = new SshClient(connection)
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

    private static IReadOnlyList<ServiceStatus> ParseServices(string output)
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

    private Task<string> ExecuteCheckedAsync(
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
