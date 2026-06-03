using System;
using System.Collections.Generic;
using System.Net;

namespace PlcsimGateway.Gui
{
    public sealed class GatewayConfig
    {
        public string defaultProfile { get; set; }
        public List<GatewayProfile> profiles { get; set; }
    }

    public sealed class GatewayProfile
    {
        public string name { get; set; }
        public string displayName { get; set; }
        public string descriptionRu { get; set; }
        public string descriptionEn { get; set; }
        public string listenAddress { get; set; }
        public int listenPort { get; set; }
        public string plcsimAddress { get; set; }
        public int rack { get; set; }
        public int slot { get; set; }
        public bool enableTsapCheck { get; set; }
        public string logPath { get; set; }
        public bool enableS7PayloadDump { get; set; }
        public string s7PayloadDumpPath { get; set; }
        public int s7PayloadDumpMaxBytes { get; set; }
        public List<string> localTsaps { get; set; }

        public override string ToString()
        {
            return GetDisplayName();
        }

        public string GetDisplayName()
        {
            if (!String.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }

            return name;
        }

        public GatewayProfile Clone()
        {
            return new GatewayProfile
            {
                name = name,
                displayName = displayName,
                descriptionRu = descriptionRu,
                descriptionEn = descriptionEn,
                listenAddress = listenAddress,
                listenPort = listenPort,
                plcsimAddress = plcsimAddress,
                rack = rack,
                slot = slot,
                enableTsapCheck = enableTsapCheck,
                logPath = logPath,
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

    public sealed class GatewayStatus
    {
        public string Profile { get; set; }
        public string Listen { get; set; }
        public string Plcsim { get; set; }
        public int? ListenerPid { get; set; }
        public int? PidFilePid { get; set; }
        public bool PidFileAlive { get; set; }
        public int ClientSessions { get; set; }
        public string LogPath { get; set; }
        public string ErrorLogPath { get; set; }
        public string StatusJsonPath { get; set; }
        public string PidFile { get; set; }
    }

    public sealed class GatewayRuntimeStatus
    {
        public string generatedAt { get; set; }
        public long totalClientPdus { get; set; }
        public long totalClientBytes { get; set; }
        public long totalPlcsimPdus { get; set; }
        public long totalPlcsimBytes { get; set; }
        public List<GatewayRuntimeSession> activeSessions { get; set; }
        public List<GatewayRuntimeSession> lastDisconnects { get; set; }
    }

    public sealed class GatewayRuntimeSession
    {
        public long sessionId { get; set; }
        public string remoteEndPoint { get; set; }
        public string protocol { get; set; }
        public long durationMs { get; set; }
        public long clientPdus { get; set; }
        public long clientBytes { get; set; }
        public long plcsimPdus { get; set; }
        public long plcsimBytes { get; set; }
        public string lastEvent { get; set; }
        public string lastMessage { get; set; }
        public string lastEventAt { get; set; }
    }

    public sealed class PowerShellResult
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }
    }
}
