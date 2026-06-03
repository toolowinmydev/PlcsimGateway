# Research Summary

PlcsimGateway was created to make Siemens PLCSIM reachable from external S7
clients over the network without starting the original NetToPLCsim GUI.

The practical target is HMI / SCADA tag access, not full PLC emulation.

## Confirmed Behavior

- External clients connect to the gateway PC over ISO-on-TCP / TCP port 102.
- The gateway unwraps the transport layer and forwards S7 payloads to PLCSIM
  through Siemens S7ONLINE.
- TIA Portal V16 / S7-1200 / WinCC Runtime traffic was observed as S7commPlus.
- For the verified HMI scenarios, full S7commPlus decoding was not required;
  passing payloads through S7ONLINE was sufficient.

## Confirmed Client Scenarios

- WinCC Runtime on a second PC.
- WinCC Runtime on the same PC as PLCSIM.
- Multiple WinCC Runtime clients at the same time.
- EasyBuilder Online Simulation on the same PC as PLCSIM.
- EasyBuilder Online Simulation from another PC.
- Physical Weintek HMI panel.

## Not Covered

- Full PLC emulation without PLCSIM.
- PROFINET discovery / Accessible Nodes.
- Engineering workflows beyond HMI / SCADA client access.
- Long-term validation across many TIA Portal versions and third-party SCADA
  runtimes.

Please treat this as a lab tool and validate your own setup before relying on
it in a workflow.
