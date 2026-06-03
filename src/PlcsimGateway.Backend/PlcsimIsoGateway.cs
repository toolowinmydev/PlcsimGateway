/*********************************************************************
 * NetToPLCsim, Netzwerkanbindung fuer PLCSIM
 *
 * Copyright (C) 2011-2016 Thomas Wiens, th.wiens@gmx.de
 *
 * This file is part of NetToPLCsim.
 *
 * NetToPLCsim is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as
 * published by the Free Software Foundation, either version 3 of the
 * License, or (at your option) any later version.
 /*********************************************************************/

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using PlcsimGateway.Backend.Tcp;
using PlcsimGateway.Backend.S7Online;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace PlcsimGateway.Backend.IsoOnTcp
{
    public class PlcsimIsoGateway : IDisposable
    {
        private TcpServer m_Server;
        private IsoServiceProvider m_Provider;
        private bool m_enableTsapCheck;
        private bool m_Disposed = false;
        public Queue<QueueEntry> m_ReceiveQueue = new Queue<QueueEntry>(100);
        public Queue<QueueEntry> m_SendQueue = new Queue<QueueEntry>(100);
        private IPAddress m_NetworkIpAdress;
        private int m_ListenPort;

        public PlcsimIsoGateway(bool enableTsapCheck)
        {
            m_enableTsapCheck = enableTsapCheck;
        }

        ~PlcsimIsoGateway()
        {
            Dispose(false);
        }

        public void start(string name, IPAddress networkIpAdress, List<byte[]> tsaps, IPAddress plcsimIp, int plcsimRackNumber, int plcsimSlotNumber)
        {
            Start(name, networkIpAdress, 102, tsaps, plcsimIp, plcsimRackNumber, plcsimSlotNumber);
        }

        public void Start(string name, IPAddress networkIpAdress, int listenPort, List<byte[]> tsaps, IPAddress plcsimIp, int plcsimRackNumber, int plcsimSlotNumber)
        {
            m_Provider = new IsoServiceProvider();
            m_Provider.ISOsrv.OnReceived = this.IsoReceived;
            m_Provider.SessionEventReceived = this.OnSessionEventReceived;
            m_Provider.ISOsrv.SetValidTsaps(tsaps);
            m_Provider.ISOsrv.EnableLocalTsapCheck = m_enableTsapCheck;
            m_NetworkIpAdress = networkIpAdress;
            m_ListenPort = listenPort;
            m_Provider.m_PlcsimIpAdress = plcsimIp;
            m_Provider.m_PlcsimRackNumber = plcsimRackNumber;
            m_Provider.m_PlcsimSlotNumber = plcsimSlotNumber;
            m_Provider.m_Name = name;

            m_Server = new TcpServer(m_Provider, listenPort);
            if (!m_Server.Start(m_NetworkIpAdress))
            {
                throw new InvalidOperationException("Failed to bind gateway listener on " + networkIpAdress + ":" + listenPort);
            }
        }

        public void stop()
        {
            Stop();
        }

        public void Stop()
        {
            if (m_Server != null)
            {
                m_Server.Stop();
                m_Server = null;
            }
        }

        public string Name { get { return m_Provider.m_Name; } }
        public IPAddress NetworkIpAdress { get { return m_NetworkIpAdress; } }
        public int ListenPort { get { return m_ListenPort; } }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!m_Disposed)
                {
                    try
                    {
                        stop();
                    }
                    catch
                    {

                    }
                }
            }
            this.m_Disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void IsoReceived(IsoServiceProvider client, byte[] data)
        {
            // Skip empty messages
            if (data.Length < 1) return;
            client.RecordClientPdu(data);

            // On the first S7 protocol telegram, determine the Plcsim version which has to be used
            // by checking the protocol type in S7 protocol header
            // 0x32 = S7comm
            // 0x72 = S7commPlus (1200/1500)
            if (client.m_S7ProtocolVersionDetected == false)
            {
                string message = String.Empty;
                bool plcsim_success = false;
                if (data[0] == 0x72)
                {
                    client.m_ProtocolName = "S7commPlus";
                    plcsim_success = client.InitPlcsim(PlcsimProtocolType.S7commPlus);
                    message = "Connecting to Plcsim using S7Comm-Plus mode for 1200/1500";
                }
                else
                {
                    client.m_ProtocolName = "S7comm";
                    plcsim_success = client.InitPlcsim(PlcsimProtocolType.S7comm);
                    message = "Connecting to Plcsim using S7Comm mode for 300/400 or 1200/1500 (not optimized)";
                }
                if (plcsim_success == false)
                {
                    client.EmitSessionEvent(IsoSessionEventType.PlcsimConnectError, "Failed to connect to Plcsim", null);
                    if (monitorDataReceived != null)
                    {
                        monitorDataReceived(client.client.RemoteEndPoint.ToString(), null, "Failed to connect to Plcsim");
                    }
                    client.client.EndConnection();
                    return;
                }
                if (monitorDataReceived != null)
                {
                    monitorDataReceived(client.client.RemoteEndPoint.ToString(), null, message);
                }
                client.EmitSessionEvent(IsoSessionEventType.ProtocolDetected, message, data);
                client.m_S7ProtocolVersionDetected = true;
            }

            PlcS7onlineMsgPump.WndProcMessage msg = new PlcS7onlineMsgPump.WndProcMessage();
            msg.pdu = data;
            msg.pdulength = data.Length;

            if (monitorDataReceived != null)
            {
                monitorDataReceived(client.client.RemoteEndPoint.ToString(), data, String.Empty);
            }
            byte[] res = null;

            // Test if we have to generate our own answer
            res = S7ProtoHook.RequestExchange(data);
            if (res == null)
            {
                client.SendDataToPlcsim(msg);
            }
            else
            {
                client.IsoSend(client.client, res);
            }
        }

        // delegate an event for monitor output
        public delegate void dataReceived(string sourceIP, byte[] data, string message);
        public event dataReceived monitorDataReceived;

        public delegate void sessionEventReceived(IsoSessionEvent sessionEvent);
        public event sessionEventReceived SessionEventReceived;

        private void OnSessionEventReceived(IsoSessionEvent sessionEvent)
        {
            if (SessionEventReceived != null)
            {
                SessionEventReceived(sessionEvent);
            }
        }
    }

    public static class IsoSessionEventType
    {
        public const string Connect = "CONNECT";
        public const string Disconnect = "DISCONNECT";
        public const string ClientPdu = "CLIENT_PDU";
        public const string PlcsimPdu = "PLCSIM_PDU";
        public const string ProtocolDetected = "PROTOCOL";
        public const string PlcsimConnectSuccess = "PLCSIM_CONNECT_SUCCESS";
        public const string PlcsimConnectError = "PLCSIM_CONNECT_ERROR";
        public const string Error = "ERROR";
    }

    public sealed class IsoSessionEvent
    {
        public string EventType { get; set; }
        public long SessionId { get; set; }
        public string StationName { get; set; }
        public string RemoteEndPoint { get; set; }
        public string ProtocolName { get; set; }
        public int PayloadLength { get; set; }
        public byte[] Payload { get; set; }
        public string FirstS7Byte { get; set; }
        public long ClientPduCount { get; set; }
        public long ClientBytes { get; set; }
        public long PlcsimPduCount { get; set; }
        public long PlcsimBytes { get; set; }
        public long DurationMilliseconds { get; set; }
        public string Message { get; set; }
    }

    public class QueueEntry
    {
        public IsoServiceProvider isoclient;
        public ConnectionState client;
        public byte[] message;
    }

    public enum PlcsimProtocolType
    {
        S7comm = 0,             // Used for Step7 V5 Plcsim, and TIA-Plcsim for 1200/1500 when using absolute address mode (put/get) -> 0x32 protocol header
        S7commPlus              // Used for TIA-Plcsimusing new protocol used for 120071500 -> 0x72 protocol header
    }

    public class IsoServiceProvider : TcpServiceProvider
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int SendMessage(IntPtr hwnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

        public PlcS7onlineMsgPump m_PlcS7onlineMsgPump;
        IntPtr m_PlcS7onlineMsgPump_Handle;
        AutoResetEvent m_autoEvent_MsgPumpThreadStart;
        AutoResetEvent m_autoEvent_ConnectPlcsim;
        bool m_ConnectPlcsimSuccess;
        private static long s_NextSessionId;
        private DateTime m_ConnectedAtUtc;
        private long m_ClientPduCount;
        private long m_ClientBytes;
        private long m_PlcsimPduCount;
        private long m_PlcsimBytes;
        public long m_SessionId;
        public int m_PlcsimRackNumber;
        public int m_PlcsimSlotNumber;
        public IPAddress m_PlcsimIpAdress;
        public bool m_S7ProtocolVersionDetected;
        public string m_ProtocolName;
        public string m_Name;
        public PlcsimIsoGateway.sessionEventReceived SessionEventReceived;

        public ISOonTCP ISOsrv = new ISOonTCP();
        public ConnectionState client;

        public IsoServiceProvider()
        {
            ISOsrv.Log = this.IsoLog;
            ISOsrv.TCPSend = this.TCPSend;
            m_PlcS7onlineMsgPump_Handle = IntPtr.Zero;
        }

        ~IsoServiceProvider()
        {
            ExitPlcsimMessagePump();
        }

        public void IsoLog(string message)
        {
            Console.WriteLine(message);
        }

        public void TCPSend(ConnectionState state, byte[] data)
        {
            client = state;
            if (!client.Write(data, 0, data.Length))
            {
                client.EndConnection();
            }
        }

        public void IsoSend(ConnectionState state, byte[] data)
        {
            try
            {
                ISOsrv.Send(state, data);
            }
            catch
            {
                client.EndConnection();
            }
        }

        public override object Clone()
        {
            IsoServiceProvider newProvider = new IsoServiceProvider();
            newProvider.ISOsrv.OnReceived = this.ISOsrv.OnReceived;   // Copy callback
            newProvider.ISOsrv.EnableLocalTsapCheck = this.ISOsrv.EnableLocalTsapCheck;
            newProvider.ISOsrv.LocalTsaps = this.ISOsrv.LocalTsaps;
            newProvider.SessionEventReceived = this.SessionEventReceived;
            newProvider.m_PlcsimIpAdress = this.m_PlcsimIpAdress;
            newProvider.m_PlcsimRackNumber = this.m_PlcsimRackNumber;
            newProvider.m_PlcsimSlotNumber = this.m_PlcsimSlotNumber;
            newProvider.m_PlcS7onlineMsgPump_Handle = this.m_PlcS7onlineMsgPump_Handle;
            newProvider.m_S7ProtocolVersionDetected = this.m_S7ProtocolVersionDetected;
            newProvider.m_ProtocolName = this.m_ProtocolName;
            newProvider.m_Name = this.m_Name;

            return newProvider;
        }

        public override void OnReceiveData(ConnectionState state)
        {
            client = state;
            byte[] buffer = new byte[1460];
            int tpktlen = 0;

            while (state.AvailableData > 0)
            {
                int readBytes;
                // Read length from TPKT header first
                readBytes = client.Read(buffer, 0, 4);
                if (readBytes > 0)
                {
                    // TPKT Header
                    if (buffer[0] == 3 && buffer[1] == 0)   // Version = 3 / Reserved = 0
                    {
                        if (BitConverter.IsLittleEndian)
                        {
                            tpktlen = ByteConvert.DoReverseEndian(BitConverter.ToUInt16(buffer, 2));
                        }
                        else
                        {
                            tpktlen = BitConverter.ToUInt16(buffer, 2);
                        }
                        // try to read the TPDU to offset 4 in buffer, this is the length from TPKT minus length of TPKT header
                        readBytes = client.Read(buffer, 4, tpktlen - 4);
                    }
                    else
                    {
                        // Wrong TPKT header
                        EmitSessionEvent(IsoSessionEventType.Error, "Wrong TPKT header", null);
                        client.EndConnection();
                        return;
                    }
                }
                // If TDPU could be read completely
                if (readBytes == (tpktlen - 4))
                {
                    try
                    {
                        ISOsrv.Process(this, buffer, readBytes + 4);
                        if (ISOsrv.Connected == false)
                        {
                            client.EndConnection();
                        }
                    }
                    catch
                    {
                        EmitSessionEvent(IsoSessionEventType.Error, "Failed to process ISO-on-TCP packet", null);
                        client.EndConnection();
                    }
                }
                else
                {
                    EmitSessionEvent(IsoSessionEventType.Error, "Incomplete TPDU read", null);
                    client.EndConnection();
                }
            }
        }

        public override void OnAcceptConnection(ConnectionState state)
        {
            client = state;
            m_SessionId = Interlocked.Increment(ref s_NextSessionId);
            m_ConnectedAtUtc = DateTime.UtcNow;
            EmitSessionEvent(IsoSessionEventType.Connect, "Client connected", null);
        }

        public override void OnDropConnection(ConnectionState state)
        {
            ExitPlcsimMessagePump();
            client = state;
            EmitSessionEvent(IsoSessionEventType.Disconnect, "Client disconnected", null);
        }

        private void ExitPlcsimMessagePump()
        {
            if (m_PlcS7onlineMsgPump_Handle != IntPtr.Zero)
            {
                SendMessage(m_PlcS7onlineMsgPump_Handle, PlcS7onlineMsgPump.WM_M_EXIT, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public bool InitPlcsim(PlcsimProtocolType plcsimVersion)
        {
            m_autoEvent_MsgPumpThreadStart = new AutoResetEvent(false);
            StartPlcS7onlineMsgPump(plcsimVersion);
            m_autoEvent_MsgPumpThreadStart.WaitOne();       // Wait until the message pumpe thread has started
            try
            {
                m_ConnectPlcsimSuccess = false;
                m_autoEvent_ConnectPlcsim = new AutoResetEvent(false);
                SendMessage(m_PlcS7onlineMsgPump_Handle, PlcS7onlineMsgPump.WM_M_CONNECTPLCSIM, IntPtr.Zero, IntPtr.Zero);
                m_autoEvent_ConnectPlcsim.WaitOne();        // Wait until a connect success or connect error was received
            }
            catch
            {
                EmitSessionEvent(IsoSessionEventType.PlcsimConnectError, "Exception while connecting to Plcsim", null);
                return false;
            }
            return m_ConnectPlcsimSuccess;
        }

        public void SendDataToPlcsim(PlcS7onlineMsgPump.WndProcMessage msg)
        {
            Int32 length = msg.pdu.Length + 4;
            byte[] buffer = new byte[length];

            byte[] pduLengthBytes = BitConverter.GetBytes(msg.pdulength);
            Buffer.BlockCopy(pduLengthBytes, 0, buffer, 0, pduLengthBytes.Length);
            Buffer.BlockCopy(msg.pdu, 0, buffer, pduLengthBytes.Length, msg.pdu.Length);

            IntPtr ptr = Marshal.AllocHGlobal(length);
            Marshal.Copy(buffer, 0, ptr, length);

            SendMessage(m_PlcS7onlineMsgPump_Handle, PlcS7onlineMsgPump.WM_M_SENDDATA, IntPtr.Zero, ptr);

            Marshal.FreeHGlobal(ptr);
        }

        public void StartPlcS7onlineMsgPump(PlcsimProtocolType plcsimVersion)
        {
            Thread PlcS7onlineMsgPumpThread = new Thread(StartPlcS7onlineMsgPumpThread);
            if (plcsimVersion == PlcsimProtocolType.S7commPlus)
            {
                m_PlcS7onlineMsgPump = new PlcS7onlineMsgPumpTia(m_PlcsimIpAdress, m_PlcsimRackNumber, m_PlcsimSlotNumber);
            }
            else
            {
                m_PlcS7onlineMsgPump = new PlcS7onlineMsgPumpS7(m_PlcsimIpAdress, m_PlcsimRackNumber, m_PlcsimSlotNumber);
            }
            m_PlcS7onlineMsgPump.eventOnDataFromPlcsimReceived += new PlcS7onlineMsgPump.OnDataFromPlcsimReceived(OnDataFromPlcsimReceived);
            PlcS7onlineMsgPumpThread.Start();
        }

        private void StartPlcS7onlineMsgPumpThread()
        {
            m_PlcS7onlineMsgPump_Handle = m_PlcS7onlineMsgPump.Handle;
            m_autoEvent_MsgPumpThreadStart.Set();
            m_PlcS7onlineMsgPump.Run();
        }

        private void OnDataFromPlcsimReceived(PlcS7onlineMsgPump.MessageFromPlcsim message)
        {
            switch (message.type)
            {
                case PlcS7onlineMsgPump.MessageFromPlcsimType.Pdu:
                    // Some telegrams may have to be modified by Nettoplcsim, e.g. response to connection setup
                    // 30.1.2016: This should be no longer needed
                    //S7ProtoHook.ResponseExchange(ref message.pdu);
                    RecordPlcsimPdu(message.pdu);
                    IsoSend(client, message.pdu);
                    break;
                case PlcS7onlineMsgPump.MessageFromPlcsimType.ConnectError:
                    m_ConnectPlcsimSuccess = false;
                    EmitSessionEvent(IsoSessionEventType.PlcsimConnectError, "Plcsim connect error", null);
                    m_autoEvent_ConnectPlcsim.Set();
                    break;
                case PlcS7onlineMsgPump.MessageFromPlcsimType.ConnectSuccess:
                    m_ConnectPlcsimSuccess = true;
                    EmitSessionEvent(IsoSessionEventType.PlcsimConnectSuccess, "Plcsim connect success", null);
                    m_autoEvent_ConnectPlcsim.Set();
                    break;
                //default:
                    // don't care about other messages at this time
                    //System.Diagnostics.Debug.Print("OnDataFromPlcsimReceived(): Type=" + message.type.ToString() + " Message=" + message.textmessage);
            }
        }

        public void RecordClientPdu(byte[] data)
        {
            m_ClientPduCount++;
            m_ClientBytes += data.Length;
            EmitSessionEvent(IsoSessionEventType.ClientPdu, String.Empty, data);
        }

        private void RecordPlcsimPdu(byte[] data)
        {
            if (data == null)
            {
                return;
            }

            m_PlcsimPduCount++;
            m_PlcsimBytes += data.Length;
            EmitSessionEvent(IsoSessionEventType.PlcsimPdu, String.Empty, data);
        }

        public void EmitSessionEvent(string eventType, string message, byte[] payload)
        {
            if (SessionEventReceived == null)
            {
                return;
            }

            IsoSessionEvent sessionEvent = new IsoSessionEvent();
            sessionEvent.EventType = eventType;
            sessionEvent.SessionId = m_SessionId;
            sessionEvent.StationName = m_Name;
            sessionEvent.RemoteEndPoint = GetRemoteEndPoint();
            sessionEvent.ProtocolName = m_ProtocolName;
            sessionEvent.PayloadLength = payload == null ? 0 : payload.Length;
            sessionEvent.Payload = payload;
            sessionEvent.FirstS7Byte = GetFirstByte(payload);
            sessionEvent.ClientPduCount = m_ClientPduCount;
            sessionEvent.ClientBytes = m_ClientBytes;
            sessionEvent.PlcsimPduCount = m_PlcsimPduCount;
            sessionEvent.PlcsimBytes = m_PlcsimBytes;
            sessionEvent.DurationMilliseconds = GetDurationMilliseconds();
            sessionEvent.Message = message;
            SessionEventReceived(sessionEvent);
        }

        private string GetRemoteEndPoint()
        {
            try
            {
                if (client != null && client.RemoteEndPoint != null)
                {
                    return client.RemoteEndPoint.ToString();
                }
            }
            catch
            {
            }

            return String.Empty;
        }

        private string GetFirstByte(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return "none";
            }

            return "0x" + payload[0].ToString("X2");
        }

        private long GetDurationMilliseconds()
        {
            if (m_ConnectedAtUtc == DateTime.MinValue)
            {
                return 0;
            }

            return (long)(DateTime.UtcNow - m_ConnectedAtUtc).TotalMilliseconds;
        }
    }
}
