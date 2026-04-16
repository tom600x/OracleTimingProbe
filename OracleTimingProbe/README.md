# OracleTimingProbe

A .NET Framework 4.8 console app that measures Oracle connectivity and query timing phases for troubleshooting application slowness.

## What It Measures

- Preflight diagnostics:
  - HOST/PORT parse from Oracle `Data Source`
  - DNS resolution
  - TCP reachability check to Oracle listener (3s timeout)
- Per-run timing:
  - `open_ms` (connection open time)
  - `exec_ms` (command execute time)
  - `read_ms` (data read time)
  - `total_ms` (end-to-end run time)
- Error diagnostics:
  - exception type/message
  - inner exception message
  - top stack frame
- CSV evidence output for customer sharing

## Project Details

- Target framework: `net48`
- Oracle provider: `Oracle.ManagedDataAccess`
- Config source: `appsettings.json`

## Configuration

Edit `appsettings.json`:

```json
{
  "Probe": {
    "ConnectionString": "User ID=...;Password=...;Data Source=(DESCRIPTION=...)",
    "TestQuery": "SELECT 1 FROM DUAL",
    "ReadRowsMax": 10,
    "Runs": 3,
    "CsvOutputPath": "probe-results.csv"
  }
}
```

### Notes

- `ConnectionString` and `TestQuery` are required.
- `CsvOutputPath` can be relative or absolute.
- If `CsvOutputPath` is empty, a timestamped file name is generated.

## Build

```powershell
dotnet build
```

## Run

```powershell
.\bin\Debug\net48\OracleTimingProbe.exe
```

## Example Console Output

```text
Oracle Timing Probe (.NET Framework 4.8)
Machine: APPVM01
User: svc_account
ProcessBitness: x86
OS: Microsoft Windows NT 10.0.17763.0
CLR: 4.0.30319.42000
OracleDriver: Oracle.ManagedDataAccess, Version=...
StartedUtc: 2026-04-16T20:45:10.1234567Z
ConnectionString(masked): User ID=***;Password=***;Data Source=(DESCRIPTION=...)
DataSourceHost: DHHBGNEDORA901-902-SCAN.PA.LCL
DataSourcePort: 1521
DnsResolved: True
DnsAddresses: 10.10.10.10,10.10.10.11
TcpConnect:3000ms Success=True
Runs: 3
ReadRowsMax: 10
----------------------------------------------------------------------
Run 1: success=True open_ms=48 exec_ms=12 read_ms=1 total_ms=63 rows=1
Run 2: success=True open_ms=8 exec_ms=10 read_ms=1 total_ms=21 rows=1
Run 3: success=True open_ms=7 exec_ms=11 read_ms=1 total_ms=21 rows=1
----------------------------------------------------------------------
CSV written: C:\path\to\probe-results.csv
```

## Interpreting Results

- High `open_ms`, low `exec_ms`: likely connection/network/pooling issue.
- Low `open_ms`, high `exec_ms`: likely SQL/database execution issue.
- High `read_ms`: large result set, fetch, or mapping overhead.
- Preflight failure + `ORA-12545`: DNS/network path issue to Oracle host.

## Troubleshooting Quick Checks

```powershell
Resolve-DnsName <oracle-scan-host>
Test-NetConnection <oracle-scan-host> -Port 1521
```

If local machine cannot reach the Oracle host, run this tool from a customer network-connected VM/jump box.
