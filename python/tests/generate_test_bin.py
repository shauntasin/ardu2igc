#!/usr/bin/env python3
"""Generate a synthetic ArduPilot .bin log for testing."""

import struct
import math

HEADER = b"\xa3\x95"
FMT_ID = 128


def make_fmt(msg_type_id, msg_len, name, fmt_str, field_names):
    """Create a FMT message."""
    name_bytes = name.encode("ascii")[:4].ljust(4, b"\x00")
    fmt_bytes = fmt_str.encode("ascii")[:16].ljust(16, b"\x00")
    fields_bytes = field_names.encode("ascii")[:200] + b"\x00"
    return struct.pack("<BB", msg_type_id, msg_len) + name_bytes + fmt_bytes + fields_bytes


def make_gps_message(time_ms, lat_deg7, lon_deg7, alt, status, hdop, week=2300):
    """Create a GPS message matching 'IBBHhhH' format."""
    return struct.pack("<IBBHhhH",
        int(time_ms),        # TimeMS (uint32)
        int(week),           # Week (uint8 - simplified)
        0,                   # GPSWeek (uint8)
        int(lat_deg7),       # Lat (int16 - simplified for test)
        int(lon_deg7),       # Lng (int16 - simplified for test)
        int(alt),            # Alt (int16)
        int(status),         # Status (uint8)
    )


def make_baro_message(time_ms, alt):
    """Create a BARO message matching 'If' format."""
    return struct.pack("<If",
        int(time_ms),
        float(alt),
    )


def generate_test_bin(output_path):
    """Generate a synthetic .bin log file."""
    messages = bytearray()

    # FMT for GPS (type 140)
    gps_fmt = make_fmt(140, 14, "GPS", "IBBHhhH", "TimeMS,Week,Status0,Lat,Lng,Alt,Status")
    messages += HEADER + bytes([FMT_ID]) + gps_fmt

    # FMT for BARO (type 130)
    baro_fmt = make_fmt(130, 12, "BARO", "If", "TimeMS,Alt")
    messages += HEADER + bytes([FMT_ID]) + baro_fmt

    # Generate 60 seconds of flight data
    base_lat = 473977  # ~47.3977 * 1e4 (simplified for int16)
    base_lon = 85456   # ~8.5456 * 1e4
    base_time = 43200000  # 12:00:00 in ms

    for i in range(60):
        t = base_time + i * 1000
        lat = base_lat + i
        lon = base_lon + i // 2
        alt = 400 + i * 2
        hdop = 120 + int(10 * math.sin(i * 0.1))

        gps_msg = make_gps_message(t, lat, lon, alt, 2, hdop)
        messages += HEADER + bytes([140]) + gps_msg

        baro_alt = alt - 5 + int(2 * math.sin(i * 0.2))
        baro_msg = make_baro_message(t, baro_alt)
        messages += HEADER + bytes([130]) + baro_msg

    with open(output_path, "wb") as f:
        f.write(messages)

    print(f"Generated test log: {output_path} ({len(messages)} bytes)")
    return output_path


if __name__ == "__main__":
    import sys
    path = sys.argv[1] if len(sys.argv) > 1 else "test_flight.bin"
    generate_test_bin(path)
