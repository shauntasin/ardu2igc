"""IGC file format writer.

Implements the FAI/IGC flight recorder file format specification.
Reference: https://xp-soaring.github.io/igc_file_format/
"""

from __future__ import annotations

import datetime
from dataclasses import dataclass, field
from pathlib import Path
from typing import List, Optional


@dataclass
class IgcFix:
    """A single GPS fix in IGC format."""

    timestamp: datetime.time  # UTC time HH:MM:SS
    latitude: float  # Decimal degrees (positive = N, negative = S)
    longitude: float  # Decimal degrees (positive = E, negative = W)
    valid: bool = True  # True = 3D fix (A), False = 2D/invalid (V)
    pressure_altitude: int = 0  # Meters, pressure altitude
    gps_altitude: int = 0  # Meters, GNSS altitude
    fix_accuracy: int = 50  # Meters, estimated position error


@dataclass
class IgcHeader:
    """IGC file header metadata."""

    date: Optional[datetime.date] = None
    pilot_name: str = ""
    glider_type: str = ""
    glider_id: str = ""
    competition_id: str = ""
    competition_class: str = ""
    firmware_version: str = "ardu2igc 1.0"
    hardware_version: str = "ArduPilot"
    fr_type: str = "ardu2igc,ArduPilot Converter"
    gps_receiver: str = "ArduPilot,Internal,16ch,10000m"
    pressure_sensor: str = "ArduPilot,Internal,11000m"
    fix_accuracy: int = 50  # Default FXA in meters
    manufacturer_code: str = "XXX"
    serial_number: str = "A2G"


class IgcWriter:
    """Writes IGC format files.

    Usage:
        writer = IgcWriter()
        writer.set_header(pilot_name="John Doe", glider_type="Quadcopter")
        for fix in fixes:
            writer.add_fix(fix)
        writer.write("output.igc")
    """

    def __init__(self):
        self._header = IgcHeader()
        self._fixes: List[IgcFix] = []

    def set_header(self, **kwargs) -> None:
        """Set header fields. Accepted kwargs match IgcHeader fields."""
        for key, value in kwargs.items():
            if hasattr(self._header, key):
                setattr(self._header, key, value)

    def add_fix(self, fix: IgcFix) -> None:
        """Add a GPS fix to the file."""
        self._fixes.append(fix)

    def add_fixes(self, fixes: list[IgcFix]) -> None:
        """Add multiple GPS fixes."""
        self._fixes.extend(fixes)

    def write(self, path: str | Path) -> None:
        """Write the IGC file."""
        path = Path(path)
        with open(path, "w", newline="\r\n") as f:
            self._write_a_record(f)
            self._write_h_records(f)
            self._write_i_record(f)
            self._write_b_records(f)

    def _write_a_record(self, f) -> None:
        """Write A record - FR manufacturer and identification."""
        mfr = self._header.manufacturer_code[:3].ljust(3)
        sn = self._header.serial_number[:3].ljust(3)
        f.write(f"A{mfr}{sn}ardu2igc Flight Recorder\r\n")

    def _write_h_records(self, f) -> None:
        """Write H records - file header."""
        h = self._header

        # Date
        if h.date:
            d = h.date
        else:
            d = datetime.date.today()
        f.write(f"HFDTE{d.strftime('%d%m%y')}\r\n")

        # Fix accuracy
        f.write(f"HFFXA{h.fix_accuracy:03d}\r\n")

        # Pilot
        if h.pilot_name:
            f.write(f"HFPLTPILOTINCHARGE:{h.pilot_name}\r\n")

        # Glider type
        if h.glider_type:
            f.write(f"HFGTYGLIDERTYPE:{h.glider_type}\r\n")

        # Glider ID
        if h.glider_id:
            f.write(f"HFGIDGLIDERID:{h.glider_id}\r\n")

        # Competition ID
        if h.competition_id:
            f.write(f"HFCIDCOMPETITIONID:{h.competition_id}\r\n")

        # Competition class
        if h.competition_class:
            f.write(f"HFCCLCOMPETITIONCLASS:{h.competition_class}\r\n")

        # GPS datum
        f.write(f"HFDTM100GPSDATUM: WGS-1984\r\n")

        # Firmware version
        f.write(f"HFRFWFIRMWAREVERSION:{h.firmware_version}\r\n")

        # Hardware version
        f.write(f"HFRHWHARDWAREVERSION:{h.hardware_version}\r\n")

        # FR type
        f.write(f"HFFTYFRTYPE:{h.fr_type}\r\n")

        # GPS receiver
        f.write(f"HFGPS{h.gps_receiver}\r\n")

        # Pressure sensor
        f.write(f"HFPRS{h.pressure_sensor}\r\n")

    def _write_i_record(self, f) -> None:
        """Write I record - extension to B record.

        We include FXA (fix accuracy) as extension byte 36-38.
        """
        f.write("I013638FXA\r\n")

    def _write_b_records(self, f) -> None:
        """Write B records - GPS fixes."""
        current_date = None

        for fix in self._fixes:
            # B record date should match the fix date if available
            # For simplicity, use the header date or today
            if current_date is None:
                if self._header.date:
                    current_date = self._header.date
                else:
                    current_date = datetime.date.today()

            # Time
            t = fix.timestamp
            time_str = f"{t.hour:02d}{t.minute:02d}{t.second:02d}"

            # Latitude: DDMMmmmN
            lat = fix.latitude
            if lat >= 0:
                lat_dir = "N"
            else:
                lat_dir = "S"
                lat = -lat

            lat_deg = int(lat)
            lat_min = (lat - lat_deg) * 60
            lat_min_int = int(lat_min)
            lat_min_frac = int((lat_min - lat_min_int) * 1000)
            lat_str = f"{lat_deg:02d}{lat_min_int:02d}{lat_min_frac:03d}{lat_dir}"

            # Longitude: DDDMMmmmE
            lon = fix.longitude
            if lon >= 0:
                lon_dir = "E"
            else:
                lon_dir = "W"
                lon = -lon

            lon_deg = int(lon)
            lon_min = (lon - lon_deg) * 60
            lon_min_int = int(lon_min)
            lon_min_frac = int((lon_min - lon_min_int) * 1000)
            lon_str = f"{lon_deg:03d}{lon_min_int:02d}{lon_min_frac:03d}{lon_dir}"

            # Validity
            validity = "A" if fix.valid else "V"

            # Press altitude (5 digits, leading zeros)
            press_alt = max(-9999, min(99999, fix.pressure_altitude))
            if press_alt < 0:
                press_alt_str = f"-{abs(press_alt):04d}"
            else:
                press_alt_str = f"{press_alt:05d}"

            # GPS altitude (5 digits, leading zeros)
            gps_alt = max(0, min(99999, fix.gps_altitude))
            gps_alt_str = f"{gps_alt:05d}"

            # Fix accuracy (FXA, 3 digits)
            fxa = max(0, min(999, fix.fix_accuracy))
            fxa_str = f"{fxa:03d}"

            b_record = (
                f"B{time_str}{lat_str}{lon_str}{validity}"
                f"{press_alt_str}{gps_alt_str}{fxa_str}"
            )
            f.write(f"{b_record}\r\n")

    def to_string(self) -> str:
        """Generate the IGC file content as a string."""
        import io

        buf = io.StringIO()
        # Write to a temporary file-like object
        # We'll build the string manually for simplicity
        lines = []
        lines.append(self._a_record_str())
        lines.extend(self._h_records_str())
        lines.append("I013638FXA")
        for fix in self._fixes:
            lines.append(self._b_record_str(fix))
        return "\r\n".join(lines) + "\r\n"

    def _a_record_str(self) -> str:
        mfr = self._header.manufacturer_code[:3].ljust(3)
        sn = self._header.serial_number[:3].ljust(3)
        return f"A{mfr}{sn}ardu2igc Flight Recorder"

    def _h_records_str(self) -> list[str]:
        h = self._header
        lines = []
        if h.date:
            d = h.date
        else:
            d = datetime.date.today()
        lines.append(f"HFDTE{d.strftime('%d%m%y')}")
        lines.append(f"HFFXA{h.fix_accuracy:03d}")
        if h.pilot_name:
            lines.append(f"HFPLTPILOTINCHARGE:{h.pilot_name}")
        if h.glider_type:
            lines.append(f"HFGTYGLIDERTYPE:{h.glider_type}")
        if h.glider_id:
            lines.append(f"HFGIDGLIDERID:{h.glider_id}")
        if h.competition_id:
            lines.append(f"HFCIDCOMPETITIONID:{h.competition_id}")
        if h.competition_class:
            lines.append(f"HFCCLCOMPETITIONCLASS:{h.competition_class}")
        lines.append("HFDTM100GPSDATUM: WGS-1984")
        lines.append(f"HFRFWFIRMWAREVERSION:{h.firmware_version}")
        lines.append(f"HFRHWHARDWAREVERSION:{h.hardware_version}")
        lines.append(f"HFFTYFRTYPE:{h.fr_type}")
        lines.append(f"HFGPS{h.gps_receiver}")
        lines.append(f"HFPRS{h.pressure_sensor}")
        return lines

    def _b_record_str(self, fix: IgcFix) -> str:
        t = fix.timestamp
        time_str = f"{t.hour:02d}{t.minute:02d}{t.second:02d}"

        lat = fix.latitude
        lat_dir = "N" if lat >= 0 else "S"
        lat = abs(lat)
        lat_deg = int(lat)
        lat_min = (lat - lat_deg) * 60
        lat_min_int = int(lat_min)
        lat_min_frac = int((lat_min - lat_min_int) * 1000)
        lat_str = f"{lat_deg:02d}{lat_min_int:02d}{lat_min_frac:03d}{lat_dir}"

        lon = fix.longitude
        lon_dir = "E" if lon >= 0 else "W"
        lon = abs(lon)
        lon_deg = int(lon)
        lon_min = (lon - lon_deg) * 60
        lon_min_int = int(lon_min)
        lon_min_frac = int((lon_min - lon_min_int) * 1000)
        lon_str = f"{lon_deg:03d}{lon_min_int:02d}{lon_min_frac:03d}{lon_dir}"

        validity = "A" if fix.valid else "V"

        press_alt = max(-9999, min(99999, fix.pressure_altitude))
        press_alt_str = f"{press_alt:05d}" if press_alt >= 0 else f"-{abs(press_alt):04d}"

        gps_alt = max(0, min(99999, fix.gps_altitude))
        gps_alt_str = f"{gps_alt:05d}"

        fxa = max(0, min(999, fix.fix_accuracy))
        fxa_str = f"{fxa:03d}"

        return f"B{time_str}{lat_str}{lon_str}{validity}{press_alt_str}{gps_alt_str}{fxa_str}"
