using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace PlcsimGateway.Gui
{
    internal static class PortConflictManager
    {
        private const int AfInet = 2;
        private const int TcpTableOwnerPidListener = 3;

        public static PortConflictInfo FindListener(string listenAddress, int listenPort)
        {
            IPAddress requestedAddress = IPAddress.Parse(listenAddress);
            foreach (TcpListenerInfo listener in GetTcpListeners())
            {
                if (listener.Port != listenPort || !MatchesAddress(listener.Address, requestedAddress))
                {
                    continue;
                }

                return BuildConflictInfo(listener.ProcessId, listener.Address, listener.Port);
            }

            return null;
        }

        public static PortTakeoverResult TakeOverPort(GatewayProfile profile, PortConflictInfo conflict)
        {
            PortTakeoverResult result = new PortTakeoverResult();
            result.Conflict = conflict;

            if (conflict.IsCurrentProcess)
            {
                throw new InvalidOperationException("TCP port is already owned by this GUI process.");
            }

            TcpListener temporaryListener = null;
            bool ownerStopped = false;
            bool ownerRestored = false;
            bool restoreAttempted = false;
            try
            {
                if (conflict.IsService)
                {
                    StopService(conflict.ServiceName);
                    result.StoppedDescription = "service " + conflict.ServiceName;
                }
                else
                {
                    StopProcess(conflict.ProcessId);
                    result.StoppedDescription = "process PID " + conflict.ProcessId;
                }

                ownerStopped = true;
                temporaryListener = ReservePort(profile.listenPort);
                restoreAttempted = true;
                RestorePortOwner(conflict, result);
                ownerRestored = true;
                Thread.Sleep(750);
            }
            catch
            {
                if (ownerStopped && !ownerRestored && !restoreAttempted)
                {
                    RestorePortOwner(conflict, result);
                }

                throw;
            }
            finally
            {
                if (temporaryListener != null)
                {
                    temporaryListener.Stop();
                }
            }

            return result;
        }

        private static void RestorePortOwner(PortConflictInfo conflict, PortTakeoverResult result)
        {
            if (conflict.IsService)
            {
                StartService(conflict.ServiceName);
                result.RestoredDescription = "service " + conflict.ServiceName;
                return;
            }

            if (conflict.CanRestartProcess)
            {
                RestartProcess(conflict.ExecutablePath, conflict.Arguments);
                result.RestoredDescription = "process " + conflict.ExecutablePath;
                return;
            }

            result.RestoredDescription = "not restored automatically";
        }

        private static PortConflictInfo BuildConflictInfo(int processId, IPAddress address, int port)
        {
            PortConflictInfo info = new PortConflictInfo();
            info.ProcessId = processId;
            info.LocalAddress = address.ToString();
            info.LocalPort = port;
            info.ProcessName = GetProcessName(processId);
            info.CommandLine = GetProcessProperty(processId, "CommandLine");
            info.ExecutablePath = GetProcessProperty(processId, "ExecutablePath");
            info.IsCurrentProcess = processId == Process.GetCurrentProcess().Id;
            ApplyRestartArguments(info);
            ApplyServiceInfo(info);
            return info;
        }

        private static void ApplyServiceInfo(PortConflictInfo info)
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT Name,DisplayName,State FROM Win32_Service WHERE ProcessId = " + info.ProcessId))
            using (ManagementObjectCollection services = searcher.Get())
            {
                foreach (ManagementObject service in services)
                {
                    info.ServiceName = Convert.ToString(service["Name"]);
                    info.ServiceDisplayName = Convert.ToString(service["DisplayName"]);
                    info.ServiceState = Convert.ToString(service["State"]);
                    return;
                }
            }
        }

        private static void ApplyRestartArguments(PortConflictInfo info)
        {
            if (String.IsNullOrWhiteSpace(info.ExecutablePath)
                || String.IsNullOrWhiteSpace(info.CommandLine))
            {
                return;
            }

            string quotedExecutable = "\"" + info.ExecutablePath + "\"";
            if (info.CommandLine.StartsWith(quotedExecutable, StringComparison.OrdinalIgnoreCase))
            {
                info.Arguments = info.CommandLine.Substring(quotedExecutable.Length).TrimStart();
                return;
            }

            if (info.CommandLine.StartsWith(info.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                info.Arguments = info.CommandLine.Substring(info.ExecutablePath.Length).TrimStart();
            }
        }

        private static string GetProcessName(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                return String.Empty;
            }
        }

        private static string GetProcessProperty(int processId, string propertyName)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT " + propertyName + " FROM Win32_Process WHERE ProcessId = " + processId))
                using (ManagementObjectCollection processes = searcher.Get())
                {
                    foreach (ManagementObject process in processes)
                    {
                        return Convert.ToString(process[propertyName]);
                    }
                }
            }
            catch
            {
            }

            return String.Empty;
        }

        private static void StopService(string serviceName)
        {
            using (ServiceController service = new ServiceController(serviceName))
            {
                service.Refresh();
                if (service.Status != ServiceControllerStatus.Stopped
                    && service.Status != ServiceControllerStatus.StopPending)
                {
                    service.Stop();
                }

                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }

        private static void StartService(string serviceName)
        {
            using (ServiceController service = new ServiceController(serviceName))
            {
                service.Refresh();
                if (service.Status != ServiceControllerStatus.Running
                    && service.Status != ServiceControllerStatus.StartPending)
                {
                    service.Start();
                }

                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
        }

        private static void StopProcess(int processId)
        {
            using (Process process = Process.GetProcessById(processId))
            {
                process.Kill();
                if (!process.WaitForExit(10000))
                {
                    throw new System.TimeoutException("Process PID " + processId + " did not stop.");
                }
            }
        }

        private static void RestartProcess(string executablePath, string arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = executablePath;
            startInfo.Arguments = arguments ?? String.Empty;
            startInfo.UseShellExecute = false;
            Process.Start(startInfo);
        }

        private static TcpListener ReservePort(int port)
        {
            TcpListener listener = new TcpListener(IPAddress.Any, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            return listener;
        }

        private static bool MatchesAddress(IPAddress listenerAddress, IPAddress requestedAddress)
        {
            return listenerAddress.Equals(IPAddress.Any)
                || requestedAddress.Equals(IPAddress.Any)
                || listenerAddress.Equals(requestedAddress);
        }

        private static IEnumerable<TcpListenerInfo> GetTcpListeners()
        {
            int bufferLength = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref bufferLength, true, AfInet, TcpTableOwnerPidListener, 0);
            IntPtr buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                result = GetExtendedTcpTable(buffer, ref bufferLength, true, AfInet, TcpTableOwnerPidListener, 0);
                if (result != 0)
                {
                    yield break;
                }

                int rowCount = Marshal.ReadInt32(buffer);
                IntPtr rowPointer = IntPtr.Add(buffer, 4);
                int rowSize = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));
                for (int i = 0; i < rowCount; i++)
                {
                    MibTcpRowOwnerPid row = (MibTcpRowOwnerPid)Marshal.PtrToStructure(rowPointer, typeof(MibTcpRowOwnerPid));
                    rowPointer = IntPtr.Add(rowPointer, rowSize);

                    TcpListenerInfo info = new TcpListenerInfo();
                    info.Address = new IPAddress(row.LocalAddress);
                    info.Port = (ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort);
                    info.ProcessId = row.OwningProcessId;
                    yield return info;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr tcpTable,
            ref int tcpTableLength,
            bool sort,
            int ipVersion,
            int tableClass,
            uint reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRowOwnerPid
        {
            public uint State;
            public uint LocalAddress;
            public uint LocalPort;
            public uint RemoteAddress;
            public uint RemotePort;
            public int OwningProcessId;
        }

        private sealed class TcpListenerInfo
        {
            public IPAddress Address;
            public int Port;
            public int ProcessId;
        }
    }

    public sealed class PortConflictInfo
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string CommandLine { get; set; }
        public string ExecutablePath { get; set; }
        public string Arguments { get; set; }
        public string ServiceName { get; set; }
        public string ServiceDisplayName { get; set; }
        public string ServiceState { get; set; }
        public string LocalAddress { get; set; }
        public int LocalPort { get; set; }
        public bool IsCurrentProcess { get; set; }

        public bool IsService
        {
            get { return !String.IsNullOrWhiteSpace(ServiceName); }
        }

        public bool CanRestartProcess
        {
            get { return !String.IsNullOrWhiteSpace(ExecutablePath); }
        }
    }

    public sealed class PortTakeoverResult
    {
        public PortConflictInfo Conflict { get; set; }
        public string StoppedDescription { get; set; }
        public string RestoredDescription { get; set; }
    }
}
