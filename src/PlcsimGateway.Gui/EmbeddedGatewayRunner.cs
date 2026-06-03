using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using PlcsimGateway.Backend.Diagnostics;
using PlcsimGateway.Backend.IsoOnTcp;

namespace PlcsimGateway.Gui
{
    internal sealed class EmbeddedGatewayRunner : IDisposable
    {
        private readonly object logLock = new object();
        private readonly object sessionLock = new object();
        private readonly object runtimeStatusWriteLock = new object();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly Dictionary<long, SessionSnapshot> sessions = new Dictionary<long, SessionSnapshot>();
        private readonly List<GatewayRuntimeSession> lastDisconnects = new List<GatewayRuntimeSession>();
        private readonly GatewayProfile profile;
        private readonly string logPath;
        private readonly string errorLogPath;
        private readonly string statusJsonPath;
        private string s7PayloadDumpPath;
        private PlcsimIsoGateway gateway;
        private Timer summaryTimer;
        private DateTime lastRuntimeStatusWriteUtc = DateTime.MinValue;
        private long clientPduCount;
        private long clientPduBytes;
        private long plcsimPduCount;
        private long plcsimPduBytes;
        private bool running;

        public EmbeddedGatewayRunner(GatewayProfile profile, string logPath, string errorLogPath, string statusJsonPath)
        {
            this.profile = profile.Clone();
            this.logPath = logPath;
            this.errorLogPath = errorLogPath;
            this.statusJsonPath = statusJsonPath;
        }

        public bool IsRunning
        {
            get { return running; }
        }

        public string LogPath
        {
            get { return logPath; }
        }

        public string ErrorLogPath
        {
            get { return errorLogPath; }
        }

        public string StatusJsonPath
        {
            get { return statusJsonPath; }
        }

        public int ActiveSessionCount
        {
            get
            {
                lock (sessionLock)
                {
                    return sessions.Count;
                }
            }
        }

        public void Start()
        {
            profile.ApplyDefaults();
            profile.Validate();

            DeleteFileIfExists(logPath);
            DeleteFileIfExists(errorLogPath);
            DeleteFileIfExists(statusJsonPath);
            s7PayloadDumpPath = ResolveS7PayloadDumpPath();
            if (profile.enableS7PayloadDump)
            {
                DeleteFileIfExists(s7PayloadDumpPath);
            }

            gateway = new PlcsimIsoGateway(profile.enableTsapCheck);
            gateway.SessionEventReceived += OnSessionEventReceived;
            gateway.Start(
                profile.name,
                IPAddress.Parse(profile.listenAddress),
                profile.listenPort,
                BuildTsaps(profile),
                IPAddress.Parse(profile.plcsimAddress),
                profile.rack,
                profile.slot);

            running = true;
            summaryTimer = new Timer(LogSummary, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

            Log("LISTEN name=" + profile.name
                + " endpoint=" + profile.listenAddress + ":" + profile.listenPort
                + " plcsim=" + profile.plcsimAddress
                + " rackSlot=" + profile.rack + "/" + profile.slot
                + " tsapCheck=" + profile.enableTsapCheck
                + " s7PayloadDump=" + BoolText(profile.enableS7PayloadDump)
                + " s7PayloadDumpPath=\"" + TextOrDash(s7PayloadDumpPath) + "\""
                + " host=embedded-gui");
            WriteRuntimeStatus(true);
        }

        public void Stop()
        {
            running = false;
            if (summaryTimer != null)
            {
                summaryTimer.Dispose();
                summaryTimer = null;
            }

            if (gateway != null)
            {
                gateway.SessionEventReceived -= OnSessionEventReceived;
                gateway.Stop();
                gateway.Dispose();
                gateway = null;
            }

            Log("STOPPED");
            ClearRuntimeStatus();
        }

        public void Dispose()
        {
            if (running || gateway != null)
            {
                Stop();
            }
        }

        private void OnSessionEventReceived(IsoSessionEvent sessionEvent)
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

        private void UpdateSessionSnapshot(IsoSessionEvent sessionEvent)
        {
            lock (sessionLock)
            {
                if (sessionEvent.EventType == IsoSessionEventType.Connect)
                {
                    sessions[sessionEvent.SessionId] = new SessionSnapshot(sessionEvent);
                    return;
                }

                SessionSnapshot snapshot;
                if (!sessions.TryGetValue(sessionEvent.SessionId, out snapshot))
                {
                    snapshot = new SessionSnapshot(sessionEvent);
                    sessions[sessionEvent.SessionId] = snapshot;
                }

                snapshot.Update(sessionEvent);
                if (sessionEvent.EventType == IsoSessionEventType.Disconnect)
                {
                    lastDisconnects.Insert(0, snapshot.ToRuntimeInfo(DateTime.UtcNow));
                    while (lastDisconnects.Count > 10)
                    {
                        lastDisconnects.RemoveAt(lastDisconnects.Count - 1);
                    }

                    sessions.Remove(sessionEvent.SessionId);
                }
            }
        }

        private void LogSummary(object state)
        {
            int activeSessions;
            long activeClientPdus = 0;
            long activeClientBytes = 0;
            long activePlcsimPdus = 0;
            long activePlcsimBytes = 0;

            lock (sessionLock)
            {
                activeSessions = sessions.Count;
                foreach (SessionSnapshot session in sessions.Values)
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

        private void WriteRuntimeStatus(bool force)
        {
            if (String.IsNullOrWhiteSpace(statusJsonPath))
            {
                return;
            }

            try
            {
                lock (runtimeStatusWriteLock)
                {
                    if (!CanWriteRuntimeStatus(force))
                    {
                        return;
                    }

                    WriteTextAtomic(statusJsonPath, serializer.Serialize(CreateRuntimeStatus()));
                }
            }
            catch (Exception ex)
            {
                LogError("runtime status write failed: " + ex.Message);
            }
        }

        private GatewayRuntimeStatus CreateRuntimeStatus()
        {
            GatewayRuntimeStatus status = new GatewayRuntimeStatus();
            status.generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            status.totalClientPdus = Interlocked.Read(ref clientPduCount);
            status.totalClientBytes = Interlocked.Read(ref clientPduBytes);
            status.totalPlcsimPdus = Interlocked.Read(ref plcsimPduCount);
            status.totalPlcsimBytes = Interlocked.Read(ref plcsimPduBytes);
            status.activeSessions = new List<GatewayRuntimeSession>();
            status.lastDisconnects = new List<GatewayRuntimeSession>();

            lock (sessionLock)
            {
                DateTime utcNow = DateTime.UtcNow;
                foreach (SessionSnapshot session in sessions.Values)
                {
                    status.activeSessions.Add(session.ToRuntimeInfo(utcNow));
                }

                foreach (GatewayRuntimeSession session in lastDisconnects)
                {
                    status.lastDisconnects.Add(session);
                }
            }

            return status;
        }

        private bool CanWriteRuntimeStatus(bool force)
        {
            DateTime utcNow = DateTime.UtcNow;
            if (!force && (utcNow - lastRuntimeStatusWriteUtc).TotalMilliseconds < 500)
            {
                return false;
            }

            lastRuntimeStatusWriteUtc = utcNow;
            return true;
        }

        private void ClearRuntimeStatus()
        {
            if (String.IsNullOrWhiteSpace(statusJsonPath))
            {
                return;
            }

            try
            {
                lock (runtimeStatusWriteLock)
                {
                    GatewayRuntimeStatus status = new GatewayRuntimeStatus();
                    status.generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    status.activeSessions = new List<GatewayRuntimeSession>();
                    lock (sessionLock)
                    {
                        status.lastDisconnects = new List<GatewayRuntimeSession>(lastDisconnects);
                    }

                    WriteTextAtomic(statusJsonPath, serializer.Serialize(status));
                }
            }
            catch (Exception ex)
            {
                LogError("runtime status clear failed: " + ex.Message);
            }
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

        private void Log(string message)
        {
            WriteLine(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message);
        }

        private void LogError(string message)
        {
            WriteLine(errorLogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " ERROR " + message);
        }

        private void WriteS7PayloadDump(string direction, IsoSessionEvent sessionEvent, long pduIndex)
        {
            if (!profile.enableS7PayloadDump || String.IsNullOrWhiteSpace(s7PayloadDumpPath))
            {
                return;
            }

            WriteLine(s7PayloadDumpPath, Utils.S7PayloadDumpLine(direction, sessionEvent, pduIndex, profile.s7PayloadDumpMaxBytes));
        }

        private string ResolveS7PayloadDumpPath()
        {
            if (!String.IsNullOrWhiteSpace(profile.s7PayloadDumpPath))
            {
                return Environment.ExpandEnvironmentVariables(profile.s7PayloadDumpPath);
            }

            if (String.IsNullOrWhiteSpace(logPath))
            {
                return String.Empty;
            }

            return Path.ChangeExtension(logPath, ".s7payload.log");
        }

        private static string BoolText(bool value)
        {
            return value ? "true" : "false";
        }

        private static string TextOrDash(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private void WriteLine(string path, string line)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                return;
            }

            lock (logLock)
            {
                string directory = Path.GetDirectoryName(path);
                if (!String.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
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

        private static void DeleteFileIfExists(string path)
        {
            if (!String.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private sealed class SessionSnapshot
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

            public GatewayRuntimeSession ToRuntimeInfo(DateTime utcNow)
            {
                GatewayRuntimeSession info = new GatewayRuntimeSession();
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
    }
}
