# Metas.Instr.Driver.Vna.LibreVNA

METAS VNA Tools driver for the LibreVNA (control via the LibreVNA-GUI's SCPI server, TCP port 19542).

## Prerequisites

- METAS VNA Tools installed (`C:\Program Files\Metas\Metas.Vna.Tools\`)
- LibreVNA-GUI running, SCPI server enabled: *Window → Preferences → General → SCPI Control → Enable server*
- No VISA required — the driver communicates directly via a TCP socket.

## Add files to folder


- Paste the `Metas.Instr.Driver.Vna.LibreVNA.dll` into `%Public%\Documents\Metas.Instr\Drivers\Release\`. 

## Usage

In the METAS VNA Tools connection dialog, select the `LibreVNA` driver (A restart might be required). The following work as the resource name:

- `localhost` (default port 19542)
- `localhost:19542` or `<host>:<port>`
- `TCPIP0::localhost::19542::SOCKET` (VISA notation is parsed, but VISA itself is not used)

The driver automatically connects the GUI to the first device found (`DEV:CONN`) and switches to VNA mode.

## Design Decisions / Limitations

- **Sweep synchronization:** SRQ is not possible over the raw TCP socket. `TriggerSingleStart` first rewrites `VNA:ACQ:AVG` (resetting `AVGLEVel` to 0 and preventing a stale `FINished? TRUE`), then writes `VNA:ACQ:SINGLE TRUE`. `TriggerSingleWait` polls `VNA:ACQ:RUN?` and `VNA:ACQ:FINished?` (100 ms interval, 2 h timeout, with BackgroundWorker cancellation support).
- **RawData:** The driver reads trace data via `VNA:TRAC:DATA?`. If a calibration is active in the LibreVNA-GUI, this data is corrected; temporarily disabling it via SCPI is not possible without deleting the calibration measurements. `GetData(VnaFormat.RawData)` therefore throws an exception if a calibration is active → reset the calibration in the GUI, METAS performs the error correction itself.
- **Parameters:** Only S11, S12, S21, S22. No wave quantities (a/b), no switch terms — the LibreVNA-GUI's SCPI interface does not expose raw receiver traces. SetUp modes using wave parameters throw a descriptive exception.
- **Stimulus:** A single shared power level for both ports (`VNA:STIM:LVL`); `Source1Power`/`Source2Power` both map to it. No power slope, no attenuators, Z0 fixed at 50 Ω.
- **No segment sweep**, no power sweep. CW operation via zero-span (`VNA:FREQ:ZERO`, frequency = center).
- **SetState/GetState** (instrument state): via a temporary `.setup` file (`DEV:SETUP:SAVE/LOAD`). Only works if LibreVNA-GUI and METAS VNA Tools run on the same machine; paths containing spaces in the temp directory can cause problems (SCPI separates parameters with spaces).
- **Frequency list:** read from the x-values of the first trace (works for LIN and LOG); fallback: computed from start/stop/points/sweep type.
- Only **one TCP client** can be connected to the LibreVNA-GUI at a time — a second connection kicks out the first.

## Trouble Shooting

If the driver does not show up in VNA Tools check: 
- Is the dll locked? Right click dll -> properties -> general -> unblock
- Is the path correct? 
