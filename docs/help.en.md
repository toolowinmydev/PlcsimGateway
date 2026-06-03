# PlcsimGateway - User Guide

PlcsimGateway.Gui.exe bridges Siemens PLCSIM to external S7 clients over the
network. The HMI, SCADA, WinCC Runtime, EasyBuilder Online Simulation, or
physical panel connects to the gateway PC IP address. The gateway then forwards
the exchange to PLCSIM through `S7ONLINE / PLCSIM.TCPIP.1`.

## Quick Start

Example:

- PLCSIM IP: `192.168.40.10`
- Network / Gateway PC IP: `192.168.40.50`
- Port: `102`

1. Start PLCSIM from the PLC project.
2. In the HMI / WinCC / SCADA connection, set the PLC address to the gateway PC
   IP, for example `192.168.40.50`.
3. Run `PlcsimGateway.Gui.exe` as Administrator.
4. Set **PLCSIM IP** to `192.168.40.10`.
5. Set **Network IP** to `192.168.40.50`.
6. Select `standard (port 102)`.
7. Click **Save IPs**.
8. Click **Start**.
9. In Siemens **Set PG/PC Interface**, set `S7ONLINE -> PLCSIM.TCPIP.1`. If
   higher-level connection entries are shown above `S7ONLINE`, set them to
   `None`.
10. Start the HMI runtime, panel, SCADA runtime, or other S7 client. The gateway
    should show an active session and increasing counters.

## Profiles

| Profile | Description |
|---------|-------------|
| `standard (port 102)` | Main mode. Listens on the network IP of this computer over TCP/102. Suitable for HMI, WinCC, EasyBuilder, SCADA, other S7 clients, and local online simulation. |
| `loopback fallback (port 1102)` | Backup local test mode. Listens on `127.0.0.1:1102`. Useful only for diagnostics on the same PC, if the client supports a custom port. |

## Main Window Fields

| Field | Meaning |
|-------|---------|
| **Network IP** | IP address of the network adapter where the gateway accepts connections. If the computer’s IP changes, update this field, press **Save IPs**, and restart the profile. |
| **PLCSIM** | CPU address inside the simulator and its rack/slot number. The current project targets a Siemens S7-1200 CPU 1215C (TIA Portal V16). |
| **PID** | Windows process ID that hosts the running gateway. |
| **Sessions** | Number of active TCP/S7 clients currently connected to the gateway. |
| **Health** | Brief status of the chain `client - gateway - PLCSIM`. |
| **Runtime** | Timestamp of the last JSON diagnostic snapshot. |

## Buttons

| Button | Action |
|--------|--------|
| **Start** | Starts the selected profile inside the GUI process. |
| **Stop** | Stops the running gateway. |
| **Restart** | Briefly drops all active sessions and restarts the same profile. |
| **Refresh** | Reloads state, logs, and runtime diagnostics. |
| **Save IPs** | Saves the current **Network IP** and **PLCSIM IP** to the configuration file. A running profile picks up the new addresses only after **Restart** or a fresh **Start**. |

## Diagnostic Counters & Indicators

- **Active sessions** — currently open client TCP/S7 sessions.
- **Last disconnects** — recently closed sessions with final transfer counters.
- **Client PDU** and **Client bytes** — increase when clients send requests towards the gateway/PLCSIM.
- **PLCSIM PDU** and **PLCSIM bytes** — increase when PLCSIM replies and the gateway forwards the response back.

> **Diagnostic tip:**  
> If client counters grow but PLCSIM counters do not, the client is sending requests, but either PLCSIM is not responding or the connection to the simulator has not yet been established.

## When TCP/102 Is Already in Use

If `Network IP:102` is occupied, **Start** will display a bilingual dialog for port release.  
Port 102 is often held by a Siemens service; therefore, the GUI requests administrator rights and can:

- temporarily stop the process holding the port,
- reserve the port for the gateway,
- restore the original port owner,
- and then start the gateway.

## Notes

This application was inspired by NetToPLCsim. It was built to make the common
PLCSIM-to-HMI lab workflow clearer and easier to repeat. Tested setups include
TIA Portal V16 PLC + WinCC HMI and TIA Portal V16 PLC + EasyBuilder HMI.

---

*Made by toolowinmydev with help from Mr. Gptinski.*
