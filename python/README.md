<p align="center">
  <img src="https://img.shields.io/badge/License-GPL%20v2-blue.svg" alt="License: GPL v2">
  <img src="https://img.shields.io/badge/Python-3.9+-blue.svg" alt="Python 3.9+">
  <img src="https://img.shields.io/badge/.NET-8.0+-purple.svg" alt=".NET 8.0+">
  <img src="https://img.shields.io/badge/ArduPilot-DataFlash-green.svg" alt="ArduPilot DataFlash">
  <img src="https://img.shields.io/badge/IGC-FAI%20Spec-orange.svg" alt="IGC FAI Spec">
</p>

<h1 align="center">ardu2igc</h1>

<p align="center">
  <strong>Convert ArduPilot .bin flight logs to IGC format — in Python or C#</strong>
</p>

<p align="center">
  <em>The universal bridge between ArduPilot telemetry and the FAI/IGC flight recorder standard.<br>
  Works with ArduCopter, ArduPlane, and any ArduPilot-based flight controller.</em>
</p>

---

## Why ardu2igc?

ArduPilot is the world's most popular open-source autopilot, powering everything from racing quads to fixed-wing survey aircraft. Its `.bin` logs contain rich flight data — but they're locked in a proprietary binary format.

**IGC** is the international standard for flight data validation, used by glider competitions, FAI record claims, and flight analysis tools worldwide.

**ardu2igc** connects these two worlds. Drop in a `.bin` file, get a standards-compliant `.igc` file out. Use it from Python or C# — same algorithm, same output.

---

## Features

- **Dual-language** — Pure Python and pure C# implementations with identical behavior
- **Zero-dependency C#** — No NuGet packages required; reads binary DataFlash directly
- **pymavlink-enhanced Python** — Uses ArduPilot's own parser when available, standalone fallback when not
- **Self-describing parser** — Discovers message schemas from FMT records inside each `.bin` file (works with any ArduPilot version)
- **Full IGC compliance** — A, H, I, B records with FXA extension per FAI specification
- **Smart altitude fusion** — Combines barometric and GPS altitude sources with configurable priority
- **Flight date extraction** — Automatically determines UTC date from GPS week numbers
- **Time gap handling** — Detects and skips large gaps in telemetry without corrupting output
- **CLI tools included** — Ready-to-use command-line converters for both languages

---

## Quick Start

### Python

```bash
# Install from source
cd ardu2igc/python
pip install -e .

# Convert a log file
ardu2igc flight.bin

# With options
ardu2igc flight.bin -o myflight.igc --pilot "Jane Doe" --glider-type "DJI Mavic 3"

# Inspect a log without converting
ardu2igc flight.bin --info
```

### C\#

```bash
# Build
cd ardu2igc/csharp
dotnet build -c Release

# Convert a log file
dotnet run --project Examples -c Release -- flight.bin

# With options
dotnet run --project Examples -c Release -- flight.bin -o myflight.igc --pilot "Jane Doe"
```

---

## Usage

### Python API

```python
import datetime
from ardu2igc import convert, ConversionOptions, IgcWriter, IgcFix

# One-liner conversion
result = convert("flight.bin", "output.igc", ConversionOptions(
    pilot_name="John Doe",
    glider_type="ArduCopter",
    altitude_source="both",
))
print(f"Wrote {result['fixes_written']} fixes")

# Programmatic IGC generation
writer = IgcWriter()
writer.set_header(
    pilot_name="John Doe",
    glider_type="Quadcopter",
    date=datetime.date(2024, 7, 15),
)
writer.add_fix(IgcFix(
    timestamp=datetime.time(14, 30, 0),
    latitude=47.39774,
    longitude=8.54559,
    valid=True,
    pressure_altitude=450,
    gps_altitude=462,
    fix_accuracy=12,
))
writer.write("manual.igc")
```

### C\# API

```csharp
using Ardu2IGC;

// One-liner conversion
var result = ArdupilotConverter.Convert("flight.bin", "output.igc", new ConversionOptions
{
    PilotName = "John Doe",
    GliderType = "ArduCopter",
    AltitudeSource = "both",
});
Console.WriteLine($"Wrote {result.FixesWritten} fixes");

// Programmatic IGC generation
var writer = new IgcWriter();
writer.Header.PilotName = "John Doe";
writer.Header.GliderType = "Quadcopter";
writer.Header.Date = new DateTime(2024, 7, 15);
writer.AddFix(new IgcFix
{
    Timestamp = new DateTime(2000, 1, 1, 14, 30, 0),
    Latitude = 47.39774,
    Longitude = 8.54559,
    Valid = true,
    PressureAltitude = 450,
    GpsAltitude = 462,
    FixAccuracy = 12,
});
writer.Write("manual.igc");
```

---

## CLI Reference

| Option | Description | Default |
|--------|-------------|---------|
| `-o, --output` | Output IGC file path | `<input>.igc` |
| `--pilot` | Pilot name | _(empty)_ |
| `--glider-type` | Aircraft type | _(empty)_ |
| `--glider-id` | Registration / ID | _(empty)_ |
| `--comp-id` | Competition ID | _(empty)_ |
| `--comp-class` | Competition class | _(empty)_ |
| `--alt-source` | Altitude source: `baro`, `gps`, `both` | `both` |
| `--max-gap` | Max gap between fixes (seconds) | `60` |
| `--fix-accuracy` | Override fix accuracy (meters) | `0` (auto) |
| `--gps2` | Use GPS2 message instead of GPS | `false` |
| `--info` | Show log info without converting | `false` |
| `--json` | JSON output (with `--info`) | `false` |

---

## How It Works

### ArduPilot .bin Format

ArduPilot DataFlash logs are self-describing binary files. Each log begins with **FMT** messages that define the schema for every message type (GPS, BARO, ATT, etc.). This means `ardu2igc` works with **any ArduPilot version** without hardcoding message definitions.

```
[0xa3 0x95] [type_id] [message_data...]
```

The parser discovers schemas at runtime:

| Message | Key Fields |
|---------|------------|
| `GPS` / `GPS2` | `Lat`, `Lng`, `Alt`, `Status`, `HDop`, `Week`, `TimeMS` |
| `BARO` / `BAR2` | `Alt`, `TimeMS` |
| `FMT` | Schema definitions (parsed automatically) |

### IGC Format

The IGC format is an ASCII-based standard defined by the FAI Gliding Commission. Each file contains:

| Record | Purpose |
|--------|---------|
| **A** | Flight recorder identification |
| **H** | Header metadata (date, pilot, glider, sensors) |
| **I** | Extension definitions for B records |
| **B** | GPS fixes (timestamp, lat, lon, altitude, accuracy) |

A valid B record looks like:

```
B1430004723864N00832735EA0045000462012
│      │       │        │ │     │    └── FXA (fix accuracy)
│      │       │        │ │     └─────── GPS altitude
│      │       │        │ └──────────── Pressure altitude
│      │       │        └────────────── Validity (A=3D, V=2D)
│      │       └─────────────────────── Longitude
│      └─────────────────────────────── Latitude
└────────────────────────────────────── Time (HH:MM:SS UTC)
```

---

## Supported ArduPilot Messages

| Message | Source | Data Extracted |
|---------|--------|----------------|
| `GPS` | GPS fix | Lat, Lng, Alt, Status, HDop, Week, TimeMS |
| `GPS2` | Secondary GPS | Same as GPS |
| `BARO` | Barometric sensor | Altitude, timestamp |
| `BAR2` | Secondary barometer | Altitude, timestamp |

---

## Project Structure

```
ardu2igc/
├── python/
│   ├── ardu2igc/
│   │   ├── __init__.py          # Package exports
│   │   ├── parser.py            # ArduPilot .bin log parser
│   │   ├── igc.py               # IGC format writer
│   │   └── converter.py         # Conversion engine
│   ├── examples/
│   │   └── convert.py           # CLI tool
│   └── pyproject.toml
├── csharp/
│   ├── Ardu2IGC/
│   │   ├── Ardu2IGC.csproj
│   │   ├── ArdupilotLogParser.cs # Binary log parser
│   │   ├── IgcWriter.cs          # IGC format writer
│   │   └── Converter.cs          # Conversion engine
│   └── Examples/
│       └── Program.cs            # CLI tool
└── LICENSE                       # GPL-2.0
```

---

## Building

### Python

```bash
# Development install
pip install -e ".[dev]"

# With pymavlink support (recommended)
pip install -e ".[pymavlink]"
```

### C\#

```bash
# Build library
dotnet build Ardu2IGC/Ardu2IGC.csproj

# Build CLI
dotnet build Examples/Examples.csproj

# Release build
dotnet build -c Release
```

---

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request. For major changes, please open an issue first to discuss what you would like to change.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

---

## License

This project is licensed under the **GNU General Public License v2.0** — see the [LICENSE](LICENSE) file for details.

Copyright (C) Sameer Somnath Sangle

---

## Acknowledgments

- [ArduPilot](https://ardupilot.org/) — The world's most popular open-source autopilot
- [FAI Gliding Commission](https://www.fai.org/commissions/gliding) — IGC format specification
- [pymavlink](https://github.com/ArduPilot/pymavlink) — MAVLink protocol and DataFlash log parser
- [IGC Format Reference](https://xp-soaring.github.io/igc_file_format/) — Developer's guide by Ian Forster-Lewis
