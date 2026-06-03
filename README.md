# PlcsimGateway

[Русская версия](README.ru.md)

PlcsimGateway is a Windows gateway for connecting external S7 clients to a
Siemens PLCSIM CPU over the network.

Typical path:

```text
WinCC / HMI / SCADA / S7 client
  -> Gateway PC IP:102
  -> PlcsimGateway.Gui.exe
  -> S7ONLINE / PLCSIM.TCPIP.1
  -> Siemens PLCSIM CPU
```

The project was developed after working with NetToPLCsim in real lab setups.
NetToPLCsim proved the idea, but the startup flow could be hard to repeat and
debug. PlcsimGateway keeps the same practical goal and wraps it in a clearer
one-window workflow: editable IPs, visible sessions, live counters, and fewer
manual steps.

The tool is published openly for lab, HMI, SCADA, and integration testing. Test
reports, fixes, ports to other environments, and feedback are welcome.

## Tested Setups

Development and testing were done with:

- TIA Portal V16 PLC project + WinCC HMI project;
- TIA Portal V16 PLC project + EasyBuilder HMI project.

Confirmed scenarios:

- PLCSIM on one PC, WinCC Runtime on another PC;
- PLCSIM and WinCC Runtime on the same PC, with optional additional WinCC
  Runtime clients on other PCs at the same time;
- PLCSIM and EasyBuilder Online Simulation on the same PC;
- PLCSIM and EasyBuilder Online Simulation on the same PC, with optional
  EasyBuilder simulation on another PC at the same time;
- PLCSIM on one PC and a physical Weintek HMI panel connected through the
  gateway, with optional simulator clients connected from other PCs.

Other TIA Portal versions, PLC families, HMI runtimes, and SCADA systems have
not been tested here because we did not have suitable projects and tools for
them. If they use the same S7 client path over ISO-on-TCP / TCP port 102, the
gateway may work, but please treat that as unverified until tested.

## Quick Start

Example addresses:

- PLCSIM CPU IP in the PLC project: `192.168.40.10`
- Gateway PC network IP: `192.168.40.50`
- Standard S7 port: `102`

Steps:

1. Start PLCSIM from your PLC project. For example, the simulated controller
   uses `192.168.40.10`.
2. In the HMI / WinCC / SCADA connection settings, set the PLC address to the
   IP of the PC where PLCSIM and PlcsimGateway are running. In this example,
   use `192.168.40.50`, not `192.168.40.10`.
3. Start `PlcsimGateway.Gui.exe` as Administrator.
4. In PlcsimGateway, set:
   - `PLCSIM IP` = `192.168.40.10`
   - `NETWORK IP` = `192.168.40.50`
5. Select `standard (port 102)`.
6. Click `Save IPs`.
7. Click `Start`.
8. In Siemens `Set PG/PC Interface`, make sure:
   - `S7ONLINE` points to `PLCSIM.TCPIP.1`;
   - all higher-level connection entries above `S7ONLINE`, if shown in your
     setup, are set to `None`.
9. Start your HMI simulation, HMI panel, WinCC Runtime, SCADA runtime, or other
   S7 client.
10. In PlcsimGateway, check that `ActiveSession` appears and that log counters
    begin to increase.

## Requirements

- Windows.
- Siemens PLCSIM installed and working.
- Siemens S7ONLINE / `S7onlinx.dll` available on the same PC.
- `S7ONLINE -> PLCSIM.TCPIP.1` configured in Siemens PG/PC Interface.
- .NET Framework 4.8.
- x86 build target.
- Administrator rights when binding TCP/102 or handling a Siemens service that
  already owns the port.

Siemens binaries, Siemens documentation, customer projects, HMI project files,
and network captures are not included in this repository.

## Build From Source

Build the GUI release:

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" `
  ".\src\PlcsimGateway.Gui\PlcsimGateway.Gui.csproj" `
  /p:Configuration=Release /p:Platform=x86 /m /v:minimal
```

The main build artifact is:

```text
src\PlcsimGateway.Gui\bin\x86\Release\PlcsimGateway.Gui.exe
```

Prebuilt Windows packages are available from the repository's
[Releases](https://github.com/toolowinmydev/PlcsimGateway/releases) page.

## Configuration

The default profile is `standard (port 102)`.

`NETWORK IP` is the IP address on the PC where external clients connect.
`PLCSIM IP` is the simulated CPU address from the PLC project.

Starter profiles are stored in:

```text
config\gateway-profiles.json
```

If you distribute only `PlcsimGateway.Gui.exe`, the GUI can create a default
`config\gateway-profiles.json` next to the exe on first start.

## Troubleshooting

- No active session appears: check that the HMI points to the gateway PC IP,
  not directly to the PLCSIM CPU IP.
- Session appears, but counters do not grow: check the HMI runtime is actually
  started and polling tags.
- Client counters grow, but PLCSIM counters do not: check PLCSIM is running,
  the PLC project is loaded, and `S7ONLINE -> PLCSIM.TCPIP.1` is selected.
- TCP/102 is busy: run the GUI as Administrator and let it handle the port
  conflict, or stop the conflicting Siemens service manually.
- External device cannot connect: check Windows Firewall and the selected
  network adapter IP.

## Scope And Limitations

PlcsimGateway is a gateway to Siemens PLCSIM. It is not a standalone PLC
emulator, a replacement for final tests on real hardware, or a full PROFINET
discovery / Accessible Nodes implementation.

Use it for lab, simulation, HMI, SCADA, and development workflows where those
limitations are acceptable.

## License And Provenance

PlcsimGateway is inspired by NetToPLCsim and includes source files derived from
NetToPLCsim, whose headers state `LGPL-3.0-or-later`. This public copy keeps
that provenance visible. See [NOTICE.md](NOTICE.md) and [LICENSE.md](LICENSE.md).

Siemens, SIMATIC, TIA Portal, PLCSIM, WinCC, and related names are trademarks of
their respective owners. This project is not affiliated with or endorsed by
Siemens.

## Credits

Made by toolowinmydev with help from Mr. Gptinski.
