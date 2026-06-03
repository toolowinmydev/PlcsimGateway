using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PlcsimGateway.Gui
{
    public sealed class GatewayController : IDisposable
    {
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private const int CommandTimeoutMilliseconds = 15000;
        private readonly Dictionary<string, EmbeddedGatewayRunner> embeddedRunners =
            new Dictionary<string, EmbeddedGatewayRunner>(StringComparer.OrdinalIgnoreCase);

        public GatewayController(string repoRoot)
        {
            RepoRoot = repoRoot;
        }

        public string RepoRoot { get; private set; }

        public string ConfigPath
        {
            get { return Path.Combine(RepoRoot, "config", "gateway-profiles.json"); }
        }

        public void Dispose()
        {
            List<string> profileNames = new List<string>(embeddedRunners.Keys);
            foreach (string profileName in profileNames)
            {
                GatewayProfile profile = null;
                try
                {
                    profile = GetProfile(profileName);
                    StopEmbedded(profile, true);
                }
                catch
                {
                    EmbeddedGatewayRunner runner;
                    if (embeddedRunners.TryGetValue(profileName, out runner))
                    {
                        runner.Dispose();
                    }

                    embeddedRunners.Remove(profileName);
                    if (profile != null)
                    {
                        DeleteFileIfExists(GetPidFilePath(profile));
                    }
                }
            }
        }

        public IReadOnlyList<GatewayProfile> LoadProfiles()
        {
            GatewayConfig config = LoadConfig();
            if (config == null || config.profiles == null)
            {
                return new List<GatewayProfile>();
            }

            return config.profiles;
        }

        public void SaveProfileAddresses(string profileName, string listenAddress, string plcsimAddress)
        {
            GatewayConfig config = LoadConfig();
            if (config == null || config.profiles == null)
            {
                throw new InvalidOperationException("Gateway config has no profiles.");
            }

            GatewayProfile profile = null;
            foreach (GatewayProfile candidate in config.profiles)
            {
                if (String.Equals(candidate.name, profileName, StringComparison.OrdinalIgnoreCase))
                {
                    profile = candidate;
                    break;
                }
            }

            if (profile == null)
            {
                throw new InvalidOperationException("Gateway profile was not found: " + profileName);
            }

            profile.listenAddress = listenAddress;
            profile.plcsimAddress = plcsimAddress;
            WriteConfig(config);
        }

        public GatewayStatus GetStatus(string profileName)
        {
            GatewayProfile profile = GetProfile(profileName);
            GatewayStatus embeddedStatus = GetEmbeddedStatus(profile);
            if (embeddedStatus != null)
            {
                return embeddedStatus;
            }

            if (!File.Exists(GetScriptPath("status-gateway.ps1")))
            {
                return GetStoppedStatus(profile);
            }

            string json = RunGatewayCommand("status-gateway.ps1", profileName, false, true).StandardOutput.Trim();
            if (String.IsNullOrWhiteSpace(json))
            {
                return GetStoppedStatus(profile);
            }

            GatewayStatus status = serializer.Deserialize<GatewayStatus>(json);
            if (status != null && String.IsNullOrWhiteSpace(status.StatusJsonPath))
            {
                status.StatusJsonPath = GetStatusJsonPath(profile);
            }

            return status;
        }

        public GatewayRuntimeStatus GetRuntimeStatus(string profileName)
        {
            string statusPath = GetStatusJsonPath(GetProfile(profileName));
            if (!File.Exists(statusPath))
            {
                return null;
            }

            try
            {
                string json = ReadSharedText(statusPath);
                if (String.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                return serializer.Deserialize<GatewayRuntimeStatus>(json);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private GatewayStatus GetEmbeddedStatus(GatewayProfile profile)
        {
            EmbeddedGatewayRunner runner;
            if (!embeddedRunners.TryGetValue(profile.name, out runner) || !runner.IsRunning)
            {
                return null;
            }

            return new GatewayStatus
            {
                Profile = profile.name,
                Listen = profile.listenAddress + ":" + profile.listenPort,
                Plcsim = profile.plcsimAddress + " rack/slot " + profile.rack + "/" + profile.slot,
                ListenerPid = Process.GetCurrentProcess().Id,
                PidFilePid = Process.GetCurrentProcess().Id,
                PidFileAlive = true,
                ClientSessions = runner.ActiveSessionCount,
                LogPath = runner.LogPath,
                ErrorLogPath = runner.ErrorLogPath,
                StatusJsonPath = runner.StatusJsonPath,
                PidFile = GetPidFilePath(profile)
            };
        }

        private GatewayStatus GetStoppedStatus(GatewayProfile profile)
        {
            return new GatewayStatus
            {
                Profile = profile.name,
                Listen = profile.listenAddress + ":" + profile.listenPort,
                Plcsim = profile.plcsimAddress + " rack/slot " + profile.rack + "/" + profile.slot,
                ListenerPid = null,
                PidFilePid = ReadPidFile(GetPidFilePath(profile)),
                PidFileAlive = false,
                ClientSessions = 0,
                LogPath = GetLogPath(profile),
                ErrorLogPath = GetErrorLogPath(profile),
                StatusJsonPath = GetStatusJsonPath(profile),
                PidFile = GetPidFilePath(profile)
            };
        }

        public PowerShellResult Start(string profileName)
        {
            GatewayProfile profile = GetProfile(profileName);
            profile.ApplyDefaults();
            profile.Validate();
            return StartEmbedded(profile);
        }

        public PowerShellResult Restart(string profileName)
        {
            GatewayProfile profile = GetProfile(profileName);
            profile.ApplyDefaults();
            profile.Validate();
            if (IsEmbeddedRunnerActive(profile))
            {
                StopEmbedded(profile, true);
            }
            else
            {
                StopInactiveProfile(profile, true);
            }

            return StartEmbedded(profile);
        }

        public PowerShellResult Stop(string profileName)
        {
            GatewayProfile profile = GetProfile(profileName);
            profile.ApplyDefaults();
            profile.Validate();
            if (IsEmbeddedRunnerActive(profile))
            {
                return StopEmbedded(profile, false);
            }

            return StopInactiveProfile(profile, false);
        }

        private GatewayProfile GetProfile(string profileName)
        {
            foreach (GatewayProfile profile in LoadProfiles())
            {
                if (String.Equals(profile.name, profileName, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }

            throw new InvalidOperationException("Gateway profile was not found: " + profileName);
        }

        private GatewayConfig LoadConfig()
        {
            EnsureConfigFile();
            return serializer.Deserialize<GatewayConfig>(File.ReadAllText(ConfigPath, Encoding.UTF8));
        }

        private void EnsureConfigFile()
        {
            if (File.Exists(ConfigPath))
            {
                return;
            }

            string directory = Path.GetDirectoryName(ConfigPath);
            if (!String.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(ConfigPath, DefaultConfigJson, new UTF8Encoding(false));
        }

        private void WriteConfig(GatewayConfig config)
        {
            File.WriteAllText(ConfigPath, BuildConfigJson(config), new UTF8Encoding(false));
        }

        private string BuildConfigJson(GatewayConfig config)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("{");
            AppendIndent(builder, 1);
            builder.Append(BuildStringProperty("defaultProfile", config.defaultProfile));
            AppendCommaAndNewLine(builder, true);
            builder.AppendLine("  \"profiles\": [");

            List<GatewayProfile> profiles = config.profiles ?? new List<GatewayProfile>();
            for (int i = 0; i < profiles.Count; i++)
            {
                AppendProfile(builder, profiles[i], i < profiles.Count - 1);
            }

            builder.AppendLine("  ]");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private void AppendProfile(StringBuilder builder, GatewayProfile profile, bool comma)
        {
            builder.AppendLine("    {");
            List<string> properties = new List<string>();
            properties.Add(BuildStringProperty("name", profile.name));
            if (!String.IsNullOrWhiteSpace(profile.displayName))
            {
                properties.Add(BuildStringProperty("displayName", profile.displayName));
            }

            if (!String.IsNullOrWhiteSpace(profile.descriptionRu))
            {
                properties.Add(BuildStringProperty("descriptionRu", profile.descriptionRu));
            }

            if (!String.IsNullOrWhiteSpace(profile.descriptionEn))
            {
                properties.Add(BuildStringProperty("descriptionEn", profile.descriptionEn));
            }

            properties.Add(BuildStringProperty("listenAddress", profile.listenAddress));
            properties.Add(BuildNumberProperty("listenPort", profile.listenPort));
            properties.Add(BuildStringProperty("plcsimAddress", profile.plcsimAddress));
            properties.Add(BuildNumberProperty("rack", profile.rack));
            properties.Add(BuildNumberProperty("slot", profile.slot));
            properties.Add(BuildBoolProperty("enableTsapCheck", profile.enableTsapCheck));
            if (profile.enableS7PayloadDump)
            {
                properties.Add(BuildBoolProperty("enableS7PayloadDump", profile.enableS7PayloadDump));
            }

            if (!String.IsNullOrWhiteSpace(profile.s7PayloadDumpPath))
            {
                properties.Add(BuildStringProperty("s7PayloadDumpPath", profile.s7PayloadDumpPath));
            }

            if (profile.s7PayloadDumpMaxBytes > 0)
            {
                properties.Add(BuildNumberProperty("s7PayloadDumpMaxBytes", profile.s7PayloadDumpMaxBytes));
            }

            if (!String.IsNullOrWhiteSpace(profile.logPath))
            {
                properties.Add(BuildStringProperty("logPath", profile.logPath));
            }

            if (profile.localTsaps != null && profile.localTsaps.Count > 0)
            {
                properties.Add(BuildStringListProperty("localTsaps", profile.localTsaps));
            }

            for (int i = 0; i < properties.Count; i++)
            {
                AppendIndent(builder, 3);
                builder.Append(properties[i]);
                AppendCommaAndNewLine(builder, i < properties.Count - 1);
            }

            builder.Append("    }");
            if (comma)
            {
                builder.Append(",");
            }

            builder.AppendLine();
        }

        private string BuildStringProperty(string name, string value)
        {
            return serializer.Serialize(name) + ": " + serializer.Serialize(value ?? String.Empty);
        }

        private static string BuildNumberProperty(string name, int value)
        {
            return "\"" + name + "\": " + value;
        }

        private static string BuildBoolProperty(string name, bool value)
        {
            return "\"" + name + "\": " + (value ? "true" : "false");
        }

        private string BuildStringListProperty(string name, IList<string> values)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(serializer.Serialize(name));
            builder.Append(": [");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(serializer.Serialize(values[i] ?? String.Empty));
            }

            builder.Append("]");
            return builder.ToString();
        }

        private static void AppendIndent(StringBuilder builder, int level)
        {
            builder.Append(new string(' ', level * 2));
        }

        private static void AppendCommaAndNewLine(StringBuilder builder, bool comma)
        {
            if (comma)
            {
                builder.Append(",");
            }

            builder.AppendLine();
        }

        private PowerShellResult StartEmbedded(GatewayProfile profile)
        {
            EmbeddedGatewayRunner existingRunner;
            if (embeddedRunners.TryGetValue(profile.name, out existingRunner) && existingRunner.IsRunning)
            {
                return CommandResult("Gateway profile '" + profile.name + "' is already running inside GUI PID "
                    + Process.GetCurrentProcess().Id + ".");
            }

            PortTakeoverResult takeoverResult = null;
            if (!IsEndpointAvailable(profile))
            {
                takeoverResult = TryTakeOverPort(profile);
                if (!IsEndpointAvailable(profile))
                {
                    throw new InvalidOperationException("Cannot start profile '" + profile.name + "': "
                        + profile.listenAddress + ":" + profile.listenPort + " is still in use.");
                }
            }

            string logPath = GetLogPath(profile);
            string errorLogPath = GetErrorLogPath(profile);
            string statusJsonPath = GetStatusJsonPath(profile);
            EmbeddedGatewayRunner runner = new EmbeddedGatewayRunner(profile, logPath, errorLogPath, statusJsonPath);
            try
            {
                runner.Start();
                embeddedRunners[profile.name] = runner;

                EnsureRuntimeDirectory();
                File.WriteAllText(GetPidFilePath(profile), Process.GetCurrentProcess().Id.ToString(), Encoding.ASCII);
                if (!WaitForEndpointState(profile, false, 5000))
                {
                    StopEmbedded(profile, true);
                    throw new TimeoutException("Gateway profile '" + profile.name + "' did not open "
                        + profile.listenAddress + ":" + profile.listenPort + ".");
                }
            }
            catch
            {
                runner.Dispose();
                embeddedRunners.Remove(profile.name);
                throw;
            }

            string takeoverMessage = takeoverResult == null ? String.Empty : FormatTakeoverResult(takeoverResult) + Environment.NewLine;
            return CommandResult(takeoverMessage + "Started embedded gateway profile '" + profile.name + "' inside GUI PID "
                + Process.GetCurrentProcess().Id
                + Environment.NewLine + "Log: " + logPath);
        }

        private PortTakeoverResult TryTakeOverPort(GatewayProfile profile)
        {
            if (profile.listenPort != 102)
            {
                throw new InvalidOperationException("Cannot start profile '" + profile.name + "': "
                    + profile.listenAddress + ":" + profile.listenPort + " is already in use.");
            }

            PortConflictInfo conflict = PortConflictManager.FindListener(profile.listenAddress, profile.listenPort);
            if (conflict == null)
            {
                throw new InvalidOperationException("Cannot start profile '" + profile.name + "': "
                    + profile.listenAddress + ":" + profile.listenPort + " is already in use, but owner PID was not found.");
            }

            if (conflict.IsCurrentProcess)
            {
                throw new InvalidOperationException("TCP 102 is already owned by this GUI process.");
            }

            if (!ConfirmPortTakeover(profile, conflict))
            {
                throw new InvalidOperationException("TCP 102 takeover was cancelled by user.");
            }

            return PortConflictManager.TakeOverPort(profile, conflict);
        }

        private static bool ConfirmPortTakeover(GatewayProfile profile, PortConflictInfo conflict)
        {
            string owner = GetPortOwnerText(conflict);
            string restoreTextRu = conflict.IsService || conflict.CanRestartProcess
                ? "\u041f\u043e\u0441\u043b\u0435 \u044d\u0442\u043e\u0433\u043e GUI \u0432\u0435\u0440\u043d\u0435\u0442 \u043e\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d\u043d\u044b\u0439 service/process \u043e\u0431\u0440\u0430\u0442\u043d\u043e, \u043a\u0430\u043a \u0434\u0435\u043b\u0430\u0435\u0442 NetToPLCsim."
                : "\u0412\u043b\u0430\u0434\u0435\u043b\u0435\u0446 \u043f\u043e\u0440\u0442\u0430 \u043d\u0435 \u043f\u043e\u0445\u043e\u0436 \u043d\u0430 service \u0438 \u043d\u0435 \u0438\u043c\u0435\u0435\u0442 \u0447\u0438\u0442\u0430\u0435\u043c\u043e\u0433\u043e executable path, \u043f\u043e\u044d\u0442\u043e\u043c\u0443 GUI \u0441\u043c\u043e\u0436\u0435\u0442 \u0442\u043e\u043b\u044c\u043a\u043e \u043e\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u044c \u0435\u0433\u043e \u0431\u0435\u0437 \u0430\u0432\u0442\u043e\u0432\u043e\u0441\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d\u0438\u044f.";
            string restoreTextEn = conflict.IsService || conflict.CanRestartProcess
                ? "After that, the GUI will restore the stopped service/process, matching NetToPLCsim behavior."
                : "The port owner does not look like a service and has no readable executable path, so the GUI can only stop it without automatic restore.";
            string actionTextRu = conflict.IsService || conflict.CanRestartProcess
                ? "\u0414\u0435\u0439\u0441\u0442\u0432\u0438\u0435: GUI \u043e\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442 \u0432\u043b\u0430\u0434\u0435\u043b\u044c\u0446\u0430 \u043f\u043e\u0440\u0442\u0430, \u0432\u0440\u0435\u043c\u0435\u043d\u043d\u043e \u0437\u0430\u0439\u043c\u0435\u0442 TCP 102, \u0437\u0430\u0442\u0435\u043c \u0437\u0430\u043f\u0443\u0441\u0442\u0438\u0442 \u0432\u043b\u0430\u0434\u0435\u043b\u044c\u0446\u0430 \u043e\u0431\u0440\u0430\u0442\u043d\u043e \u0438 \u043f\u043e\u0441\u043b\u0435 \u044d\u0442\u043e\u0433\u043e \u043f\u043e\u0434\u043d\u0438\u043c\u0435\u0442 gateway."
                : "\u0414\u0435\u0439\u0441\u0442\u0432\u0438\u0435: GUI \u043e\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442 \u0432\u043b\u0430\u0434\u0435\u043b\u044c\u0446\u0430 \u043f\u043e\u0440\u0442\u0430, \u0432\u0440\u0435\u043c\u0435\u043d\u043d\u043e \u0437\u0430\u0439\u043c\u0435\u0442 TCP 102 \u0438 \u043f\u043e\u0441\u043b\u0435 \u044d\u0442\u043e\u0433\u043e \u043f\u043e\u0434\u043d\u0438\u043c\u0435\u0442 gateway.";
            string actionTextEn = conflict.IsService || conflict.CanRestartProcess
                ? "Action: the GUI will stop the port owner, temporarily reserve TCP 102, then start the owner again and bring the gateway up."
                : "Action: the GUI will stop the port owner, temporarily reserve TCP 102, and then bring the gateway up.";

            string message = BuildPortTakeoverMessage(
                profile.name,
                owner,
                actionTextRu,
                restoreTextRu,
                actionTextEn,
                restoreTextEn);

            return MessageBox.Show(
                message,
                "TCP 102 \u0437\u0430\u043d\u044f\u0442 / TCP 102 busy",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private static string BuildPortTakeoverMessage(
            string profileName,
            string owner,
            string actionTextRu,
            string restoreTextRu,
            string actionTextEn,
            string restoreTextEn)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("TCP 102 \u0437\u0430\u043d\u044f\u0442, \u043f\u0440\u043e\u0444\u0438\u043b\u044c '" + profileName + "' \u043d\u0435 \u0441\u043c\u043e\u0436\u0435\u0442 \u0441\u0442\u0430\u0440\u0442\u043e\u0432\u0430\u0442\u044c.");
            builder.AppendLine();
            builder.AppendLine("\u0412\u043b\u0430\u0434\u0435\u043b\u0435\u0446 \u043f\u043e\u0440\u0442\u0430:");
            builder.AppendLine(owner);
            builder.AppendLine();
            builder.AppendLine("\u041e\u0441\u0432\u043e\u0431\u043e\u0434\u0438\u0442\u044c TCP 102?");
            builder.AppendLine();
            builder.AppendLine(actionTextRu);
            builder.AppendLine();
            builder.AppendLine(restoreTextRu);
            builder.AppendLine();
            builder.AppendLine("TCP 102 is busy, so profile '" + profileName + "' cannot start.");
            builder.AppendLine();
            builder.AppendLine("Port owner:");
            builder.AppendLine(owner);
            builder.AppendLine();
            builder.AppendLine("Free TCP 102?");
            builder.AppendLine();
            builder.AppendLine(actionTextEn);
            builder.AppendLine();
            builder.Append(restoreTextEn);
            return builder.ToString();
        }

        private static string GetPortOwnerText(PortConflictInfo conflict)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("PID ").Append(conflict.ProcessId);
            if (!String.IsNullOrWhiteSpace(conflict.ProcessName))
            {
                builder.Append(" (").Append(conflict.ProcessName).Append(")");
            }

            builder.AppendLine();
            builder.Append("Endpoint: ").Append(conflict.LocalAddress).Append(":").Append(conflict.LocalPort).AppendLine();
            if (conflict.IsService)
            {
                builder.Append("Service: ").Append(conflict.ServiceName);
                if (!String.IsNullOrWhiteSpace(conflict.ServiceDisplayName))
                {
                    builder.Append(" / ").Append(conflict.ServiceDisplayName);
                }

                builder.AppendLine();
            }

            if (!String.IsNullOrWhiteSpace(conflict.ExecutablePath))
            {
                builder.Append("Path: ").Append(conflict.ExecutablePath).AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private static string FormatTakeoverResult(PortTakeoverResult result)
        {
            return "TCP 102 takeover: stopped " + result.StoppedDescription
                + "; restored " + result.RestoredDescription + ".";
        }

        private bool IsEmbeddedRunnerActive(GatewayProfile profile)
        {
            EmbeddedGatewayRunner runner;
            return embeddedRunners.TryGetValue(profile.name, out runner) && runner.IsRunning;
        }

        private PowerShellResult StopEmbedded(GatewayProfile profile, bool quiet)
        {
            EmbeddedGatewayRunner runner;
            if (!embeddedRunners.TryGetValue(profile.name, out runner) || !runner.IsRunning)
            {
                embeddedRunners.Remove(profile.name);
                DeleteFileIfExists(GetPidFilePath(profile));
                DeleteFileIfExists(GetStatusJsonPath(profile));
                if (quiet)
                {
                    return CommandResult(String.Empty);
                }

                return CommandResult("Gateway profile '" + profile.name + "' is not running inside this GUI.");
            }

            runner.Stop();
            runner.Dispose();
            embeddedRunners.Remove(profile.name);
            DeleteFileIfExists(GetPidFilePath(profile));
            WaitForEndpointState(profile, true, 5000);

            if (quiet)
            {
                return CommandResult(String.Empty);
            }

            return CommandResult("Stopped embedded gateway profile '" + profile.name + "' inside GUI PID "
                + Process.GetCurrentProcess().Id + ".");
        }

        private PowerShellResult StartDirect(GatewayProfile profile)
        {
            string pidFile = GetPidFilePath(profile);
            int? existingPid = ReadPidFile(pidFile);
            Process existingProcess = GetGatewayProcess(existingPid);
            if (existingProcess != null)
            {
                return CommandResult("Gateway profile '" + profile.name + "' is already running as PID " + existingProcess.Id + ".");
            }

            if (!IsEndpointAvailable(profile))
            {
                throw new InvalidOperationException("Cannot start profile '" + profile.name + "': "
                    + profile.listenAddress + ":" + profile.listenPort + " is already in use.");
            }

            string logPath = GetLogPath(profile);
            string errorLogPath = GetErrorLogPath(profile);
            string statusJsonPath = GetStatusJsonPath(profile);
            DeleteFileIfExists(logPath);
            DeleteFileIfExists(errorLogPath);
            DeleteFileIfExists(statusJsonPath);

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = GetGatewayExecutablePath();
            startInfo.WorkingDirectory = RepoRoot;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.Arguments = JoinProcessArguments(new[]
            {
                "--profile", profile.name,
                "--config", ConfigPath,
                "--log", logPath,
                "--error-log", errorLogPath,
                "--status-json", statusJsonPath
            });

            Process process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("PlcsimGateway.Host process did not start.");
            }

            EnsureRuntimeDirectory();
            File.WriteAllText(pidFile, process.Id.ToString(), Encoding.ASCII);
            if (!WaitForEndpointState(profile, false, 5000))
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException("Gateway profile '" + profile.name + "' exited during startup. Recent log: "
                        + ReadTail(logPath, 20));
                }

                throw new TimeoutException("Gateway profile '" + profile.name + "' did not open "
                    + profile.listenAddress + ":" + profile.listenPort + ".");
            }

            return CommandResult("Started gateway profile '" + profile.name + "' PID " + process.Id
                + Environment.NewLine + "Log: " + logPath);
        }

        private PowerShellResult StopInactiveProfile(GatewayProfile profile, bool quiet)
        {
            string pidFile = GetPidFilePath(profile);
            int? pid = ReadPidFile(pidFile);
            string staleReason;
            Process process = GetGatewayProcess(pid, out staleReason);
            if (process == null)
            {
                DeleteFileIfExists(pidFile);
                DeleteFileIfExists(GetStatusJsonPath(profile));
                if (quiet)
                {
                    return CommandResult(String.Empty);
                }

                return CommandResult(GetInactiveStopMessage(profile, staleReason));
            }

            return StopDirect(profile, quiet);
        }

        private PowerShellResult StopDirect(GatewayProfile profile, bool quiet)
        {
            string pidFile = GetPidFilePath(profile);
            int? pid = ReadPidFile(pidFile);
            Process process = GetGatewayProcess(pid);
            if (process == null)
            {
                DeleteFileIfExists(pidFile);
                if (quiet)
                {
                    return CommandResult(String.Empty);
                }

                return CommandResult("Gateway profile '" + profile.name + "' is not running.");
            }

            int processId = process.Id;
            process.Kill();
            if (!process.WaitForExit(5000))
            {
                throw new TimeoutException("Gateway profile '" + profile.name + "' PID " + processId + " did not stop.");
            }

            DeleteFileIfExists(pidFile);
            DeleteFileIfExists(GetStatusJsonPath(profile));
            WaitForEndpointState(profile, true, 5000);
            if (quiet)
            {
                return CommandResult(String.Empty);
            }

            return CommandResult("Stopped gateway profile '" + profile.name + "' PID " + processId + ".");
        }

        private static string GetInactiveStopMessage(GatewayProfile profile, string staleReason)
        {
            if (String.IsNullOrWhiteSpace(staleReason))
            {
                return "Gateway profile '" + profile.name + "' is not running.";
            }

            return "Gateway profile '" + profile.name + "' is not running; removed stale PID file. " + staleReason;
        }

        private static PowerShellResult CommandResult(string output)
        {
            return new PowerShellResult
            {
                ExitCode = 0,
                StandardOutput = output,
                StandardError = String.Empty
            };
        }

        public string ReadTail(string path, int maxLines)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return String.Empty;
            }

            try
            {
                string text = ReadTailText(path, 256 * 1024);
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                int start = Math.Max(0, lines.Length - maxLines);
                return String.Join(Environment.NewLine, Subset(lines, start, lines.Length - start));
            }
            catch (IOException ex)
            {
                return "Cannot read log: " + ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                return "Cannot read log: " + ex.Message;
            }
        }

        private static string ReadTailText(string path, int maxBytes)
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length == 0)
                {
                    return String.Empty;
                }

                int bytesToRead = (int)Math.Min(stream.Length, maxBytes);
                byte[] buffer = new byte[bytesToRead];
                stream.Seek(-bytesToRead, SeekOrigin.End);
                int bytesRead = stream.Read(buffer, 0, bytesToRead);
                return Encoding.UTF8.GetString(buffer, 0, bytesRead);
            }
        }

        private static string ReadSharedText(string path)
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private string GetGatewayExecutablePath()
        {
            string[] candidates =
            {
                Path.Combine(RepoRoot, "src", "PlcsimGateway.Host", "bin", "x86", "Release", "PlcsimGateway.Host.exe"),
                Path.Combine(RepoRoot, "src", "PlcsimGateway.Host", "bin", "x86", "Debug", "PlcsimGateway.Host.exe")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException("PlcsimGateway.Host.exe was not found. Build PlcsimGateway.Host first.");
        }

        private string GetPidFilePath(GatewayProfile profile)
        {
            return Path.Combine(GetRuntimeDirectory(), "gateway-" + GetSafeName(profile.name) + ".pid");
        }

        private string GetLogPath(GatewayProfile profile)
        {
            if (!String.IsNullOrWhiteSpace(profile.logPath))
            {
                return Environment.ExpandEnvironmentVariables(profile.logPath);
            }

            string endpoint = GetSafeName(profile.listenAddress) + "-" + profile.listenPort;
            return Path.Combine(GetRuntimeDirectory(), "gateway-" + GetSafeName(profile.name) + "-" + endpoint + ".log");
        }

        private string GetErrorLogPath(GatewayProfile profile)
        {
            return Path.ChangeExtension(GetLogPath(profile), ".err.log");
        }

        private string GetStatusJsonPath(GatewayProfile profile)
        {
            return Path.ChangeExtension(GetLogPath(profile), ".status.json");
        }

        private string GetRuntimeDirectory()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string tempRoot = String.IsNullOrWhiteSpace(localAppData)
                ? Path.GetTempPath()
                : Path.Combine(localAppData, "Temp");

            return Path.Combine(tempRoot, "nettoplcsim-gateway");
        }

        private void EnsureRuntimeDirectory()
        {
            Directory.CreateDirectory(GetRuntimeDirectory());
        }

        private static string GetSafeName(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return "default";
            }

            StringBuilder builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if ((character >= 'a' && character <= 'z')
                    || (character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9')
                    || character == '.'
                    || character == '_'
                    || character == '-')
                {
                    builder.Append(character);
                }
                else
                {
                    builder.Append('_');
                }
            }

            return builder.ToString();
        }

        private static int? ReadPidFile(string pidFile)
        {
            if (!File.Exists(pidFile))
            {
                return null;
            }

            int pid;
            if (Int32.TryParse(File.ReadAllText(pidFile, Encoding.ASCII).Trim(), out pid))
            {
                return pid;
            }

            return null;
        }

        private static Process GetGatewayProcess(int? processId, out string staleReason)
        {
            staleReason = String.Empty;
            if (!processId.HasValue)
            {
                return null;
            }

            try
            {
                Process process = Process.GetProcessById(processId.Value);
                if (process.ProcessName.IndexOf("PlcsimGateway.Host", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return process;
                }

                staleReason = "PID file pointed to PID " + processId.Value
                    + ", but that process is '" + process.ProcessName + "', not PlcsimGateway.Host.";
                return null;
            }
            catch (ArgumentException)
            {
                staleReason = "PID file pointed to PID " + processId.Value + ", but that process no longer exists.";
                return null;
            }
        }

        private static Process GetGatewayProcess(int? processId)
        {
            string staleReason;
            return GetGatewayProcess(processId, out staleReason);
        }

        private static bool WaitForEndpointState(GatewayProfile profile, bool available, int timeoutMilliseconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (IsEndpointAvailable(profile) == available)
                {
                    return true;
                }

                System.Threading.Thread.Sleep(250);
            }

            return IsEndpointAvailable(profile) == available;
        }

        private static bool IsEndpointAvailable(GatewayProfile profile)
        {
            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Parse(profile.listenAddress), profile.listenPort));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            finally
            {
                if (socket != null)
                {
                    socket.Close();
                }
            }
        }

        private static void DeleteFileIfExists(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            File.Delete(path);
        }

        private static string JoinProcessArguments(IEnumerable<string> arguments)
        {
            StringBuilder builder = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(QuoteProcessArgument(argument));
            }

            return builder.ToString();
        }

        private static string QuoteProcessArgument(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private PowerShellResult RunGatewayCommand(string scriptName, string profileName, bool restart, bool asJson)
        {
            string scriptPath = GetScriptPath(scriptName);
            StringBuilder invocation = new StringBuilder();
            invocation.Append("& ");
            invocation.Append(QuotePowerShell(scriptPath));
            invocation.Append(" -Profile ");
            invocation.Append(QuotePowerShell(profileName));

            if (restart)
            {
                invocation.Append(" -Restart");
            }

            StringBuilder command = new StringBuilder();
            command.Append("$ErrorActionPreference='Stop'; ");
            if (asJson)
            {
                command.Append("$result = ");
                command.Append(invocation);
                command.Append("; $result | ConvertTo-Json -Depth 4 -Compress");
            }
            else
            {
                command.Append(invocation);
            }

            return RunPowerShell(command.ToString());
        }

        private string GetScriptPath(string scriptName)
        {
            return Path.Combine(RepoRoot, "tools", scriptName);
        }

        private PowerShellResult RunPowerShell(string command)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = GetPowerShellPath();
            startInfo.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + QuoteCommandArgument(command);
            startInfo.WorkingDirectory = RepoRoot;
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("PowerShell process did not start.");
                }

                StringBuilder stdout = new StringBuilder();
                StringBuilder stderr = new StringBuilder();
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        stdout.AppendLine(e.Data);
                    }
                };

                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        stderr.AppendLine(e.Data);
                    }
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(CommandTimeoutMilliseconds))
                {
                    TryKill(process);
                    process.WaitForExit(2000);
                    CancelAsyncReads(process);

                    throw new TimeoutException("PowerShell command timed out.");
                }

                process.WaitForExit(1000);
                CancelAsyncReads(process);

                if (process.ExitCode != 0)
                {
                    string errorText = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
                    throw new InvalidOperationException(errorText);
                }

                return new PowerShellResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = stdout.ToString(),
                    StandardError = stderr.ToString()
                };
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill();
            }
            catch
            {
            }
        }

        private static void CancelAsyncReads(Process process)
        {
            try
            {
                process.CancelOutputRead();
            }
            catch
            {
            }

            try
            {
                process.CancelErrorRead();
            }
            catch
            {
            }
        }

        private static string[] Subset(string[] values, int start, int count)
        {
            string[] result = new string[count];
            Array.Copy(values, start, result, 0, count);
            return result;
        }

        private static string GetPowerShellPath()
        {
            string candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            return "powershell.exe";
        }

        private static string QuotePowerShell(string value)
        {
            return "'" + value.Replace("'", "''") + "'";
        }

        private static string QuoteCommandArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private const string DefaultConfigJson =
            "{\n"
            + "  \"defaultProfile\": \"external\",\n"
            + "  \"profiles\": [\n"
            + "    {\n"
            + "      \"name\": \"external\",\n"
            + "      \"displayName\": \"standard (port 102)\",\n"
            + "      \"descriptionRu\": \"\u041e\u0441\u043d\u043e\u0432\u043d\u043e\u0439 \u0440\u0435\u0436\u0438\u043c: HMI, WinCC, EasyBuilder, SCADA \u0438\u043b\u0438 \u0434\u0440\u0443\u0433\u043e\u0439 S7-\u043a\u043b\u0438\u0435\u043d\u0442 \u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0430\u0435\u0442\u0441\u044f \u043a IP \u044d\u0442\u043e\u0433\u043e \u041f\u041a \u043f\u043e TCP/102.\",\n"
            + "      \"descriptionEn\": \"Main mode: HMI, WinCC, EasyBuilder, SCADA, or another S7 client connects to this PC IP over TCP/102.\",\n"
            + "      \"listenAddress\": \"192.168.40.50\",\n"
            + "      \"listenPort\": 102,\n"
            + "      \"plcsimAddress\": \"192.168.40.10\",\n"
            + "      \"rack\": 0,\n"
            + "      \"slot\": 1,\n"
            + "      \"enableTsapCheck\": false\n"
            + "    },\n"
            + "    {\n"
            + "      \"name\": \"internal\",\n"
            + "      \"displayName\": \"loopback fallback (port 1102)\",\n"
            + "      \"descriptionRu\": \"\u0420\u0435\u0437\u0435\u0440\u0432\u043d\u044b\u0439 \u043b\u043e\u043a\u0430\u043b\u044c\u043d\u044b\u0439 \u0440\u0435\u0436\u0438\u043c: \u0442\u043e\u043b\u044c\u043a\u043e \u043a\u043b\u0438\u0435\u043d\u0442\u044b \u043d\u0430 \u044d\u0442\u043e\u043c \u0436\u0435 \u041f\u041a \u0447\u0435\u0440\u0435\u0437 127.0.0.1:1102, \u0435\u0441\u043b\u0438 \u043a\u043b\u0438\u0435\u043d\u0442 \u0443\u043c\u0435\u0435\u0442 \u0443\u043a\u0430\u0437\u0430\u0442\u044c \u043d\u0435\u0441\u0442\u0430\u043d\u0434\u0430\u0440\u0442\u043d\u044b\u0439 \u043f\u043e\u0440\u0442.\",\n"
            + "      \"descriptionEn\": \"Fallback local mode: same-PC clients only through 127.0.0.1:1102 if the client can use a custom port.\",\n"
            + "      \"listenAddress\": \"127.0.0.1\",\n"
            + "      \"listenPort\": 1102,\n"
            + "      \"plcsimAddress\": \"192.168.40.10\",\n"
            + "      \"rack\": 0,\n"
            + "      \"slot\": 1,\n"
            + "      \"enableTsapCheck\": false\n"
            + "    }\n"
            + "  ]\n"
            + "}\n";
    }
}
