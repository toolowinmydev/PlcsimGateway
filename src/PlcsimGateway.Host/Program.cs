using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using PlcsimGateway.Backend.Diagnostics;
using PlcsimGateway.Backend.IsoOnTcp;

namespace PlcsimGateway.Host
{
    internal static class Program
    {
        private static readonly object LogLock = new object();
        private static readonly object SessionLock = new object();
        private static readonly object RuntimeStatusWriteLock = new object();
        private static readonly Dictionary<long, SessionSnapshot> Sessions = new Dictionary<long, SessionSnapshot>();
        private static readonly List<RuntimeSessionInfo> LastDisconnects = new List<RuntimeSessionInfo>();
        private static readonly JavaScriptSerializer RuntimeStatusSerializer = new JavaScriptSerializer();
        private static string RuntimeStatusPath;
        private static string S7PayloadDumpPath;
        private static int S7PayloadDumpMaxBytes;
        private static bool EnableS7PayloadDump;
        private static DateTime lastRuntimeStatusWriteUtc = DateTime.MinValue;
        private static long clientPduCount;
        private static long clientPduBytes;
        private static long plcsimPduCount;
        private static long plcsimPduBytes;

        private static int Main(string[] args)
        {
            TextWriter logWriter = null;
            TextWriter errorWriter = null;
            try
            {
                Console.OutputEncoding = Encoding.UTF8;

                CliOptions options = CliOptions.Parse(args);
                if (options.ShowHelp)
                {
                    PrintHelp();
                    return 0;
                }

                ConfigureConsoleFiles(options, out logWriter, out errorWriter);
                RuntimeStatusPath = options.StatusJsonPath;

                GatewayProfile profile = LoadProfile(options.ConfigPath, options.ProfileName);
                options.ApplyTo(profile);
                profile.ApplyDefaults();
                profile.Validate();
                ConfigureS7PayloadDump(profile, options.LogPath);

                List<byte[]> tsaps = BuildTsaps(profile);
                using (PlcsimIsoGateway gateway = new PlcsimIsoGateway(profile.enableTsapCheck))
                using (ManualResetEvent shutdown = new ManualResetEvent(false))
                using (Timer summaryTimer = new Timer(LogSummary, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)))
                {
                    Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
                    {
                        eventArgs.Cancel = true;
                        shutdown.Set();
                    };

                    gateway.SessionEventReceived += OnSessionEventReceived;
                    gateway.Start(
                        profile.name,
                        IPAddress.Parse(profile.listenAddress),
                        profile.listenPort,
                        tsaps,
                        IPAddress.Parse(profile.plcsimAddress),
                        profile.rack,
                        profile.slot);

                    Log("LISTEN name=" + profile.name
                        + " endpoint=" + profile.listenAddress + ":" + profile.listenPort
                        + " plcsim=" + profile.plcsimAddress
                        + " rackSlot=" + profile.rack + "/" + profile.slot
                        + " tsapCheck=" + profile.enableTsapCheck
                        + " s7PayloadDump=" + BoolText(EnableS7PayloadDump)
                        + " s7PayloadDumpPath=\"" + TextOrDash(S7PayloadDumpPath) + "\"");
                    Log("Press Ctrl+C to stop.");
                    WriteRuntimeStatus(true);

                    shutdown.WaitOne();
                    gateway.Stop();
                }

                Log("STOPPED");
                ClearRuntimeStatus();
                return 0;
            }
            catch (Exception ex)
            {
                Log("ERROR " + ex.GetType().Name + ": " + ex.Message);
                WriteRuntimeStatus(true);
                return 1;
            }
            finally
            {
                if (logWriter != null)
                {
                    logWriter.Dispose();
                }

                if (errorWriter != null)
                {
                    errorWriter.Dispose();
                }
            }
        }

        private static void ConfigureConsoleFiles(CliOptions options, out TextWriter logWriter, out TextWriter errorWriter)
        {
            logWriter = null;
            errorWriter = null;

            if (!String.IsNullOrWhiteSpace(options.LogPath))
            {
                logWriter = CreateLogWriter(options.LogPath);
                Console.SetOut(logWriter);
            }

            if (!String.IsNullOrWhiteSpace(options.ErrorLogPath))
            {
                errorWriter = CreateLogWriter(options.ErrorLogPath);
                Console.SetError(errorWriter);
            }
        }

        private static TextWriter CreateLogWriter(string path)
        {
            string expandedPath = Environment.ExpandEnvironmentVariables(path);
            string directory = Path.GetDirectoryName(expandedPath);
            if (!String.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            FileStream stream = new FileStream(
                expandedPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.AutoFlush = true;
            return writer;
        }

        private static GatewayProfile LoadProfile(string configPath, string profileName)
        {
            string resolvedPath = ResolveConfigPath(configPath);
            if (!File.Exists(resolvedPath))
            {
                throw new FileNotFoundException("Gateway config not found", resolvedPath);
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            GatewayConfig config = serializer.Deserialize<GatewayConfig>(File.ReadAllText(resolvedPath, Encoding.UTF8));
            if (config == null || config.profiles == null)
            {
                throw new InvalidOperationException("Gateway config has no profiles: " + resolvedPath);
            }

            foreach (GatewayProfile profile in config.profiles)
            {
                if (String.Equals(profile.name, profileName, StringComparison.OrdinalIgnoreCase))
                {
                    return profile.Clone();
                }
            }

            throw new InvalidOperationException("Profile not found: " + profileName);
        }

        private static string ResolveConfigPath(string configPath)
        {
            if (Path.IsPathRooted(configPath))
            {
                return configPath;
            }

            string fromCurrentDirectory = Path.GetFullPath(configPath);
            if (File.Exists(fromCurrentDirectory))
            {
                return fromCurrentDirectory;
            }

            string fromExecutable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configPath);
            return Path.GetFullPath(fromExecutable);
        }

        private static List<byte[]> BuildTsaps(GatewayProfile profile)
        {
            if (profile.localTsaps != null && profile.localTsaps.Count > 0)
            {
                List<byte[]> configuredTsaps = new List<byte[]>();
                foreach (string tsap in profile.localTsaps)
                {
                    configuredTsaps.Add(ParseHexBytes(tsap));
                }

                return configuredTsaps;
            }

            byte rackSlot = (byte)((profile.rack << 4) | profile.slot);
            return new List<byte[]>
            {
                new byte[] { 0x01, rackSlot },
                new byte[] { 0x02, rackSlot },
                new byte[] { 0x03, rackSlot }
            };
        }

        private static byte[] ParseHexBytes(string value)
        {
            string normalized = value.Replace(" ", String.Empty)
                .Replace("-", String.Empty)
                .Replace(":", String.Empty);
            if (normalized.Length == 0 || normalized.Length % 2 != 0)
            {
                throw new FormatException("Invalid TSAP hex value: " + value);
            }

            byte[] result = new byte[normalized.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Convert.ToByte(normalized.Substring(i * 2, 2), 16);
            }

            return result;
        }

        private static void OnSessionEventReceived(IsoSessionEvent sessionEvent)
        {
            UpdateSessionSnapshot(sessionEvent);

            if (sessionEvent.EventType == IsoSessionEventType.Connect)
            {
                Log("SESSION event=connect"
                    + " id=" + sessionEvent.SessionId
                    + " remote=" + sessionEvent.RemoteEndPoint);
                WriteRuntimeStatus(true);
                return;
            }

            if (sessionEvent.EventType == IsoSessionEventType.Disconnect)
            {
                Log("SESSION event=disconnect"
                    + " id=" + sessionEvent.SessionId
                    + " remote=" + sessionEvent.RemoteEndPoint
                    + " durationMs=" + sessionEvent.DurationMilliseconds
                    + " clientPdus=" + sessionEvent.ClientPduCount
                    + " clientBytes=" + sessionEvent.ClientBytes
                    + " plcsimPdus=" + sessionEvent.PlcsimPduCount
                    + " plcsimBytes=" + sessionEvent.PlcsimBytes);
                WriteRuntimeStatus(true);
                return;
            }

            if (sessionEvent.EventType == IsoSessionEventType.ClientPdu)
            {
                Interlocked.Increment(ref clientPduCount);
                Interlocked.Add(ref clientPduBytes, sessionEvent.PayloadLength);
                Log("CLIENT"
                    + " session=" + sessionEvent.SessionId
                    + " source=" + sessionEvent.RemoteEndPoint
                    + " pduBytes=" + sessionEvent.PayloadLength
                    + " firstS7=" + sessionEvent.FirstS7Byte
                    + " sessionPdus=" + sessionEvent.ClientPduCount
                    + " sessionBytes=" + sessionEvent.ClientBytes
                    + " totalPdus=" + Interlocked.Read(ref clientPduCount)
                    + " totalBytes=" + Interlocked.Read(ref clientPduBytes));
                WriteS7PayloadDump("client->plcsim", sessionEvent, sessionEvent.ClientPduCount);
                WriteRuntimeStatus(false);
                return;
            }

            if (sessionEvent.EventType == IsoSessionEventType.PlcsimPdu)
            {
                Interlocked.Increment(ref plcsimPduCount);
                Interlocked.Add(ref plcsimPduBytes, sessionEvent.PayloadLength);
                WriteS7PayloadDump("plcsim->client", sessionEvent, sessionEvent.PlcsimPduCount);
                WriteRuntimeStatus(false);
                return;
            }

            if (sessionEvent.EventType == IsoSessionEventType.ProtocolDetected)
            {
                Log("SESSION event=protocol"
                    + " id=" + sessionEvent.SessionId
                    + " remote=" + sessionEvent.RemoteEndPoint
                    + " protocol=" + sessionEvent.ProtocolName
                    + " message=\"" + sessionEvent.Message + "\"");
                WriteRuntimeStatus(true);
                return;
            }

            if (sessionEvent.EventType == IsoSessionEventType.PlcsimConnectSuccess)
            {
                Log("SESSION event=plcsim-connect-success"
                    + " id=" + sessionEvent.SessionId
                    + " remote=" + sessionEvent.RemoteEndPoint
                    + " protocol=" + sessionEvent.ProtocolName);
                WriteRuntimeStatus(true);
                return;
            }

            if (sessionEvent.EventType == IsoSessionEventType.PlcsimConnectError)
            {
                Log("SESSION event=plcsim-connect-error"
                    + " id=" + sessionEvent.SessionId
                    + " remote=" + sessionEvent.RemoteEndPoint
                    + " protocol=" + sessionEvent.ProtocolName
                    + " message=\"" + sessionEvent.Message + "\"");
                WriteRuntimeStatus(true);
                return;
            }

            if (sessionEvent.EventType == IsoSessionEventType.Error)
            {
                Log("SESSION event=error"
                    + " id=" + sessionEvent.SessionId
                    + " remote=" + sessionEvent.RemoteEndPoint
                    + " message=\"" + sessionEvent.Message + "\"");
                WriteRuntimeStatus(true);
            }
        }

        private static void UpdateSessionSnapshot(IsoSessionEvent sessionEvent)
        {
            lock (SessionLock)
            {
                if (sessionEvent.EventType == IsoSessionEventType.Connect)
                {
                    Sessions[sessionEvent.SessionId] = new SessionSnapshot(sessionEvent);
                    return;
                }

                SessionSnapshot snapshot;
                if (!Sessions.TryGetValue(sessionEvent.SessionId, out snapshot))
                {
                    snapshot = new SessionSnapshot(sessionEvent);
                    Sessions[sessionEvent.SessionId] = snapshot;
                }

                snapshot.Update(sessionEvent);
                if (sessionEvent.EventType == IsoSessionEventType.Disconnect)
                {
                    LastDisconnects.Insert(0, snapshot.ToRuntimeInfo(DateTime.UtcNow));
                    while (LastDisconnects.Count > 10)
                    {
                        LastDisconnects.RemoveAt(LastDisconnects.Count - 1);
                    }

                    Sessions.Remove(sessionEvent.SessionId);
                }
            }
        }

        private static void LogSummary(object state)
        {
            int activeSessions;
            long activeClientPdus = 0;
            long activeClientBytes = 0;
            long activePlcsimPdus = 0;
            long activePlcsimBytes = 0;

            lock (SessionLock)
            {
                activeSessions = Sessions.Count;
                foreach (SessionSnapshot session in Sessions.Values)
                {
                    activeClientPdus += session.ClientPduCount;
                    activeClientBytes += session.ClientBytes;
                    activePlcsimPdus += session.PlcsimPduCount;
                    activePlcsimBytes += session.PlcsimBytes;
                }
            }

            Log("SUMMARY"
                + " activeSessions=" + activeSessions
                + " activeClientPdus=" + activeClientPdus
                + " activeClientBytes=" + activeClientBytes
                + " activePlcsimPdus=" + activePlcsimPdus
                + " activePlcsimBytes=" + activePlcsimBytes
                + " totalClientPdus=" + Interlocked.Read(ref clientPduCount)
                + " totalClientBytes=" + Interlocked.Read(ref clientPduBytes)
                + " totalPlcsimPdus=" + Interlocked.Read(ref plcsimPduCount)
                + " totalPlcsimBytes=" + Interlocked.Read(ref plcsimPduBytes));
            WriteRuntimeStatus(true);
        }

        private static void WriteRuntimeStatus(bool force)
        {
            if (String.IsNullOrWhiteSpace(RuntimeStatusPath))
            {
                return;
            }

            try
            {
                lock (RuntimeStatusWriteLock)
                {
                    if (!CanWriteRuntimeStatus(force))
                    {
                        return;
                    }

                    RuntimeStatus status = new RuntimeStatus();
                    status.generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    status.totalClientPdus = Interlocked.Read(ref clientPduCount);
                    status.totalClientBytes = Interlocked.Read(ref clientPduBytes);
                    status.totalPlcsimPdus = Interlocked.Read(ref plcsimPduCount);
                    status.totalPlcsimBytes = Interlocked.Read(ref plcsimPduBytes);
                    status.activeSessions = new List<RuntimeSessionInfo>();
                    status.lastDisconnects = new List<RuntimeSessionInfo>();

                    lock (SessionLock)
                    {
                        DateTime utcNow = DateTime.UtcNow;
                        foreach (SessionSnapshot session in Sessions.Values)
                        {
                            status.activeSessions.Add(session.ToRuntimeInfo(utcNow));
                        }

                        foreach (RuntimeSessionInfo session in LastDisconnects)
                        {
                            status.lastDisconnects.Add(session);
                        }
                    }

                    string json = RuntimeStatusSerializer.Serialize(status);
                    WriteTextAtomic(RuntimeStatusPath, json);
                }
            }
            catch
            {
            }
        }

        private static bool CanWriteRuntimeStatus(bool force)
        {
            DateTime utcNow = DateTime.UtcNow;
            if (!force && (utcNow - lastRuntimeStatusWriteUtc).TotalMilliseconds < 500)
            {
                return false;
            }

            lastRuntimeStatusWriteUtc = utcNow;
            return true;
        }

        private static void ClearRuntimeStatus()
        {
            if (String.IsNullOrWhiteSpace(RuntimeStatusPath))
            {
                return;
            }

            try
            {
                lock (RuntimeStatusWriteLock)
                {
                    RuntimeStatus status = new RuntimeStatus();
                    status.generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    status.activeSessions = new List<RuntimeSessionInfo>();
                    lock (SessionLock)
                    {
                        status.lastDisconnects = new List<RuntimeSessionInfo>(LastDisconnects);
                    }

                    WriteTextAtomic(RuntimeStatusPath, RuntimeStatusSerializer.Serialize(status));
                }
            }
            catch
            {
            }
        }

        private static void WriteTextAtomic(string path, string text)
        {
            string expandedPath = Environment.ExpandEnvironmentVariables(path);
            string directory = Path.GetDirectoryName(expandedPath);
            if (!String.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = expandedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, text, new UTF8Encoding(false));
            if (File.Exists(expandedPath))
            {
                File.Delete(expandedPath);
            }

            File.Move(tempPath, expandedPath);
        }

        private static void ConfigureS7PayloadDump(GatewayProfile profile, string logPath)
        {
            EnableS7PayloadDump = profile.enableS7PayloadDump;
            S7PayloadDumpMaxBytes = profile.s7PayloadDumpMaxBytes;
            S7PayloadDumpPath = ResolveS7PayloadDumpPath(profile, logPath);
            if (EnableS7PayloadDump && !String.IsNullOrWhiteSpace(S7PayloadDumpPath))
            {
                string expandedPath = Environment.ExpandEnvironmentVariables(S7PayloadDumpPath);
                if (File.Exists(expandedPath))
                {
                    File.Delete(expandedPath);
                }
            }
        }

        private static string ResolveS7PayloadDumpPath(GatewayProfile profile, string logPath)
        {
            if (!String.IsNullOrWhiteSpace(profile.s7PayloadDumpPath))
            {
                return Environment.ExpandEnvironmentVariables(profile.s7PayloadDumpPath);
            }

            if (!String.IsNullOrWhiteSpace(logPath))
            {
                return Path.ChangeExtension(Environment.ExpandEnvironmentVariables(logPath), ".s7payload.log");
            }

            return String.Empty;
        }

        private static void WriteS7PayloadDump(string direction, IsoSessionEvent sessionEvent, long pduIndex)
        {
            if (!EnableS7PayloadDump || String.IsNullOrWhiteSpace(S7PayloadDumpPath))
            {
                return;
            }

            WriteLine(S7PayloadDumpPath, Utils.S7PayloadDumpLine(direction, sessionEvent, pduIndex, S7PayloadDumpMaxBytes));
        }

        private static void WriteLine(string path, string line)
        {
            string expandedPath = Environment.ExpandEnvironmentVariables(path);
            string directory = Path.GetDirectoryName(expandedPath);
            if (!String.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (LogLock)
            {
                File.AppendAllText(expandedPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }

        private static string BoolText(bool value)
        {
            return value ? "true" : "false";
        }

        private static string TextOrDash(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static void Log(string message)
        {
            lock (LogLock)
            {
                Console.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message);
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("PlcsimGateway.Host");
            Console.WriteLine("  --profile <name>            Profile name from config/gateway-profiles.json");
            Console.WriteLine("  --config <path>             Config path");
            Console.WriteLine("  --listen-address <ip>       Override listen address");
            Console.WriteLine("  --listen-port <port>        Override listen TCP port");
            Console.WriteLine("  --plcsim-address <ip>       Override PLCSIM address used through S7ONLINE");
            Console.WriteLine("  --rack <number>             Override rack");
            Console.WriteLine("  --slot <number>             Override slot");
            Console.WriteLine("  --tsap-check <true|false>   Override destination TSAP check");
            Console.WriteLine("  --log <path>                Write stdout log to file");
            Console.WriteLine("  --error-log <path>          Write stderr log to file");
            Console.WriteLine("  --status-json <path>        Write runtime session snapshot to file");
        }
    }

    public sealed class GatewayConfig
    {
        public List<GatewayProfile> profiles;
    }

    public sealed class GatewayProfile
    {
        public string name;
        public string listenAddress;
        public int listenPort;
        public string plcsimAddress;
        public int rack;
        public int slot;
        public bool enableTsapCheck;
        public bool enableS7PayloadDump;
        public string s7PayloadDumpPath;
        public int s7PayloadDumpMaxBytes;
        public List<string> localTsaps;

        public GatewayProfile Clone()
        {
            return new GatewayProfile
            {
                name = name,
                listenAddress = listenAddress,
                listenPort = listenPort,
                plcsimAddress = plcsimAddress,
                rack = rack,
                slot = slot,
                enableTsapCheck = enableTsapCheck,
                enableS7PayloadDump = enableS7PayloadDump,
                s7PayloadDumpPath = s7PayloadDumpPath,
                s7PayloadDumpMaxBytes = s7PayloadDumpMaxBytes,
                localTsaps = localTsaps == null ? null : new List<string>(localTsaps)
            };
        }

        public void ApplyDefaults()
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                name = "PLC#001";
            }

            if (String.IsNullOrWhiteSpace(listenAddress))
            {
                listenAddress = "0.0.0.0";
            }

            if (listenPort == 0)
            {
                listenPort = 102;
            }

            if (String.IsNullOrWhiteSpace(plcsimAddress))
            {
                plcsimAddress = "192.168.40.10";
            }
        }

        public void Validate()
        {
            IPAddress.Parse(listenAddress);
            IPAddress.Parse(plcsimAddress);

            if (listenPort < 1 || listenPort > 65535)
            {
                throw new ArgumentOutOfRangeException("listenPort", "Listen port must be in range 1..65535.");
            }

            if (rack < 0 || rack > 15)
            {
                throw new ArgumentOutOfRangeException("rack", "Rack must be in range 0..15.");
            }

            if (slot < 0 || slot > 15)
            {
                throw new ArgumentOutOfRangeException("slot", "Slot must be in range 0..15.");
            }
        }
    }

    internal sealed class CliOptions
    {
        public string ConfigPath = Path.Combine("config", "gateway-profiles.json");
        public string ProfileName = "external";
        public string LogPath;
        public string ErrorLogPath;
        public string StatusJsonPath;
        public bool ShowHelp;

        private string listenAddress;
        private int? listenPort;
        private string plcsimAddress;
        private int? rack;
        private int? slot;
        private bool? enableTsapCheck;

        public static CliOptions Parse(string[] args)
        {
            CliOptions options = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--help" || arg == "-h" || arg == "/?")
                {
                    options.ShowHelp = true;
                    continue;
                }

                if (arg == "--config")
                {
                    options.ConfigPath = ReadValue(args, ref i, arg);
                }
                else if (arg == "--profile")
                {
                    options.ProfileName = ReadValue(args, ref i, arg);
                }
                else if (arg == "--listen-address")
                {
                    options.listenAddress = ReadValue(args, ref i, arg);
                }
                else if (arg == "--listen-port")
                {
                    options.listenPort = Int32.Parse(ReadValue(args, ref i, arg));
                }
                else if (arg == "--plcsim-address")
                {
                    options.plcsimAddress = ReadValue(args, ref i, arg);
                }
                else if (arg == "--rack")
                {
                    options.rack = Int32.Parse(ReadValue(args, ref i, arg));
                }
                else if (arg == "--slot")
                {
                    options.slot = Int32.Parse(ReadValue(args, ref i, arg));
                }
                else if (arg == "--tsap-check")
                {
                    options.enableTsapCheck = Boolean.Parse(ReadValue(args, ref i, arg));
                }
                else if (arg == "--log")
                {
                    options.LogPath = ReadValue(args, ref i, arg);
                }
                else if (arg == "--error-log")
                {
                    options.ErrorLogPath = ReadValue(args, ref i, arg);
                }
                else if (arg == "--status-json")
                {
                    options.StatusJsonPath = ReadValue(args, ref i, arg);
                }
                else
                {
                    throw new ArgumentException("Unknown argument: " + arg);
                }
            }

            return options;
        }

        public void ApplyTo(GatewayProfile profile)
        {
            if (listenAddress != null)
            {
                profile.listenAddress = listenAddress;
            }

            if (listenPort.HasValue)
            {
                profile.listenPort = listenPort.Value;
            }

            if (plcsimAddress != null)
            {
                profile.plcsimAddress = plcsimAddress;
            }

            if (rack.HasValue)
            {
                profile.rack = rack.Value;
            }

            if (slot.HasValue)
            {
                profile.slot = slot.Value;
            }

            if (enableTsapCheck.HasValue)
            {
                profile.enableTsapCheck = enableTsapCheck.Value;
            }
        }

        private static string ReadValue(string[] args, ref int index, string optionName)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException("Missing value for " + optionName);
            }

            index++;
            return args[index];
        }
    }

    internal sealed class SessionSnapshot
    {
        public long SessionId;
        public string RemoteEndPoint;
        public string ProtocolName;
        public long ClientPduCount;
        public long ClientBytes;
        public long PlcsimPduCount;
        public long PlcsimBytes;
        public string LastEventType;
        public string LastMessage;
        public DateTime ConnectedAtUtc;
        public DateTime LastEventAtUtc;

        public SessionSnapshot(IsoSessionEvent sessionEvent)
        {
            SessionId = sessionEvent.SessionId;
            ConnectedAtUtc = DateTime.UtcNow;
            Update(sessionEvent);
        }

        public void Update(IsoSessionEvent sessionEvent)
        {
            LastEventAtUtc = DateTime.UtcNow;
            RemoteEndPoint = sessionEvent.RemoteEndPoint;
            ProtocolName = sessionEvent.ProtocolName;
            ClientPduCount = sessionEvent.ClientPduCount;
            ClientBytes = sessionEvent.ClientBytes;
            PlcsimPduCount = sessionEvent.PlcsimPduCount;
            PlcsimBytes = sessionEvent.PlcsimBytes;
            LastEventType = sessionEvent.EventType;
            LastMessage = sessionEvent.Message;
        }

        public RuntimeSessionInfo ToRuntimeInfo(DateTime utcNow)
        {
            RuntimeSessionInfo info = new RuntimeSessionInfo();
            info.sessionId = SessionId;
            info.remoteEndPoint = RemoteEndPoint;
            info.protocol = ProtocolName;
            info.durationMs = ConnectedAtUtc == DateTime.MinValue ? 0 : (long)(utcNow - ConnectedAtUtc).TotalMilliseconds;
            info.clientPdus = ClientPduCount;
            info.clientBytes = ClientBytes;
            info.plcsimPdus = PlcsimPduCount;
            info.plcsimBytes = PlcsimBytes;
            info.lastEvent = LastEventType;
            info.lastMessage = LastMessage;
            info.lastEventAt = LastEventAtUtc == DateTime.MinValue ? String.Empty : LastEventAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
            return info;
        }
    }

    internal sealed class RuntimeStatus
    {
        public string generatedAt;
        public long totalClientPdus;
        public long totalClientBytes;
        public long totalPlcsimPdus;
        public long totalPlcsimBytes;
        public List<RuntimeSessionInfo> activeSessions;
        public List<RuntimeSessionInfo> lastDisconnects;
    }

    internal sealed class RuntimeSessionInfo
    {
        public long sessionId;
        public string remoteEndPoint;
        public string protocol;
        public long durationMs;
        public long clientPdus;
        public long clientBytes;
        public long plcsimPdus;
        public long plcsimBytes;
        public string lastEvent;
        public string lastMessage;
        public string lastEventAt;
    }
}
