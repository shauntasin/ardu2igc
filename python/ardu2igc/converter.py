"""Convert ArduPilot .bin logs to IGC format."""

from __future__ import annotations

import datetime
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

from .parser import ArdupilotLog, LogEntry
from .igc import IgcFix, IgcWriter, IgcHeader


@dataclass
class ConversionOptions:
    """Options for log conversion."""

    pilot_name: str = ""
    glider_type: str = ""
    glider_id: str = ""
    competition_id: str = ""
    competition_class: str = ""

    # Altitude source: "baro" uses BARO AltChr, "gps" uses GPS Alt, "both" uses baro for press, gps for gps
    altitude_source: str = "both"

    # Minimum GPS fix age to include (seconds). Filters out stale fixes.
    min_gps_age: float = 0.0

    # Maximum time gap between fixes (seconds). Inserts gaps in output.
    max_time_gap: float = 60.0

    # Fix accuracy override (meters). If 0, uses value from GPS message.
    fix_accuracy_override: int = 0

    # Use GPS2 message instead of GPS
    use_gps2: bool = False


def _gps_week_to_date(gps_week: int, gps_tow_ms: float) -> datetime.date:
    """Convert GPS week number and time-of-week to a UTC date."""
    # GPS epoch: January 6, 1980
    import datetime as _dt

    gps_epoch = _dt.date(1980, 1, 6)
    days = gps_week * 7 + int(gps_tow_ms / 86400000)
    return gps_epoch + _dt.timedelta(days=days)


def _time_ms_to_time(time_ms: float) -> datetime.time:
    """Convert milliseconds since midnight to a datetime.time."""
    total_secs = int(time_ms / 1000)
    hours = (total_secs // 3600) % 24
    minutes = (total_secs % 3600) // 60
    seconds = total_secs % 60
    return datetime.time(hours, minutes, seconds)


def _parse_gps_message(entry: LogEntry) -> Optional[IgcFix]:
    """Parse GPS or GPS2 message into an IgcFix."""
    fields = entry.fields

    # Latitude/longitude in degrees * 10^7
    lat_raw = fields.get("Lat")
    lon_raw = fields.get("Lng")
    if lat_raw is None or lon_raw is None:
        return None

    lat = float(lat_raw) / 1e7
    lon = float(lon_raw) / 1e7

    # Altitude
    alt = int(fields.get("Alt", 0))

    # Fix type: 0=no fix, 1=2D, 2=3D
    fix_type = fields.get("Status", fields.get("FixType", 0))
    valid = int(fix_type) >= 2

    # Time
    # GPS messages may have TimeMS (ms since midnight) or T (ms since week start)
    time_ms = fields.get("TimeMS") or fields.get("T")
    if time_ms is not None:
        ts = _time_ms_to_time(float(time_ms))
    else:
        ts = entry.timestamp_ms / 1000.0
        total_secs = int(ts) % 86400
        ts = datetime.time(
            (total_secs // 3600) % 24,
            (total_secs % 3600) // 60,
            total_secs % 60,
        )

    # Fix accuracy (HDop * ~5m or from message)
    hdop = fields.get("HDop", 1.0)
    if hdop is None or hdop <= 0:
        hdop = 1.0
    accuracy = int(float(hdop) * 5.0)

    return IgcFix(
        timestamp=ts,
        latitude=lat,
        longitude=lon,
        valid=valid,
        pressure_altitude=0,
        gps_altitude=alt,
        fix_accuracy=accuracy,
    )


def _parse_baro_message(entry: LogEntry) -> Optional[int]:
    """Parse BARO message, return pressure altitude in meters."""
    fields = entry.fields

    # BARO messages have Alt (barometric altitude)
    alt = fields.get("Alt")
    if alt is not None:
        return int(float(alt))

    # Some BARO variants use AltChr
    alt_chr = fields.get("AltChr")
    if alt_chr is not None:
        return int(float(alt_chr))

    return None


def _parse_gps_time(entry: LogEntry) -> Optional[tuple[int, float]]:
    """Extract GPS week and time-of-week from a GPS message."""
    fields = entry.fields
    week = fields.get("Week") or fields.get("GWk")
    time_ms = fields.get("TimeMS") or fields.get("T") or fields.get("GMS")

    if week and time_ms and int(week) > 0:
        return (int(week), float(time_ms))
    return None


def convert(
    input_path: str | Path,
    output_path: str | Path,
    options: Optional[ConversionOptions] = None,
) -> dict:
    """Convert an ArduPilot .bin log file to IGC format.

    Args:
        input_path: Path to the ArduPilot .bin log file.
        output_path: Path for the output .igc file.
        options: Conversion options.

    Returns:
        Dictionary with conversion statistics.
    """
    if options is None:
        options = ConversionOptions()

    log = ArdupilotLog(input_path)
    writer = IgcWriter()

    # Set header
    header_kwargs = {}
    if options.pilot_name:
        header_kwargs["pilot_name"] = options.pilot_name
    if options.glider_type:
        header_kwargs["glider_type"] = options.glider_type
    if options.glider_id:
        header_kwargs["glider_id"] = options.glider_id
    if options.competition_id:
        header_kwargs["competition_id"] = options.competition_id
    if options.competition_class:
        header_kwargs["competition_class"] = options.competition_class

    writer.set_header(**header_kwargs)

    # Try to extract flight date from GPS
    gps_time = log.get_first_gps_time()
    if gps_time:
        gps_week, _ = gps_time
        # Use today's date as fallback; actual date extraction needs GPS week calc
        pass

    # Collect barometric readings keyed approximately by time
    baro_data: dict[float, int] = {}
    gps_type = "GPS2" if options.use_gps2 else "GPS"

    # First pass: collect barometric data
    for entry in log.iter_messages("BARO", "BAR2"):
        ts = entry.timestamp_ms
        alt = _parse_baro_message(entry)
        if alt is not None:
            baro_data[ts] = alt

    # Second pass: process GPS fixes
    fix_count = 0
    skip_count = 0
    last_gps_time = None
    flight_date = None

    for entry in log.iter_messages(gps_type, "GPS"):
        gps_fix = _parse_gps_message(entry)
        if gps_fix is None:
            continue

        # Extract date from GPS week
        gps_info = _parse_gps_time(entry)
        if gps_info and flight_date is None:
            gps_week, gps_tow_ms = gps_info
            if gps_week > 0:
                flight_date = _gps_week_to_date(gps_week, gps_tow_ms)

        # Check time gap
        current_time = (
            gps_fix.timestamp.hour * 3600
            + gps_fix.timestamp.minute * 60
            + gps_fix.timestamp.second
        )
        if last_gps_time is not None:
            gap = current_time - last_gps_time
            if gap < 0:
                gap += 86400  # Handle midnight crossing
            if gap > options.max_time_gap:
                skip_count += 1
                last_gps_time = current_time
                continue

        # Find nearest barometric reading
        if options.altitude_source in ("baro", "both"):
            nearest_baro_ts = None
            min_diff = float("inf")
            for baro_ts in baro_data:
                diff = abs(baro_ts - entry.timestamp_ms)
                if diff < min_diff:
                    min_diff = diff
                    nearest_baro_ts = baro_ts

            if nearest_baro_ts is not None and min_diff < 5000:
                gps_fix.pressure_altitude = baro_data[nearest_baro_ts]
            else:
                # Use GPS altitude as fallback for pressure
                gps_fix.pressure_altitude = gps_fix.gps_altitude

        if options.altitude_source == "baro":
            gps_fix.gps_altitude = gps_fix.pressure_altitude
        elif options.altitude_source == "gps":
            gps_fix.pressure_altitude = gps_fix.gps_altitude

        # Apply fix accuracy override
        if options.fix_accuracy_override > 0:
            gps_fix.fix_accuracy = options.fix_accuracy_override

        writer.add_fix(gps_fix)
        fix_count += 1
        last_gps_time = current_time

    # Set flight date on header
    if flight_date:
        writer.set_header(date=flight_date)

    # Write output
    writer.write(output_path)

    return {
        "input": str(input_path),
        "output": str(output_path),
        "fixes_written": fix_count,
        "gaps_skipped": skip_count,
        "flight_date": str(flight_date) if flight_date else "unknown",
    }


def convert_info(input_path: str | Path) -> dict:
    """Get information about a .bin log file without converting.

    Returns:
        Dictionary with log file information.
    """
    log = ArdupilotLog(input_path)

    msg_counts = {}
    gps_info = None
    baro_count = 0

    for entry in log.iter_messages():
        mtype = entry.msg_type
        msg_counts[mtype] = msg_counts.get(mtype, 0) + 1

        if mtype in ("GPS", "GPS2") and gps_info is None:
            gps_info = _parse_gps_time(entry)

    for entry in log.iter_messages("BARO", "BAR2"):
        baro_count += 1

    return {
        "file": str(input_path),
        "message_types": len(msg_counts),
        "total_messages": sum(msg_counts.values()),
        "message_counts": dict(sorted(msg_counts.items())),
        "has_gps": "GPS" in msg_counts or "GPS2" in msg_counts,
        "has_baro": baro_count > 0,
        "baro_readings": baro_count,
        "gps_info": gps_info,
    }
