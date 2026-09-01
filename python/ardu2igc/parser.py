"""ArduPilot DataFlash .bin log parser.

Supports two backends:
  1. pymavlink (preferred) - robust, well-tested
  2. Standalone fallback - no external dependencies
"""

from __future__ import annotations

import struct
import datetime
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterator, Optional

_PACKET_HEADER = b"\xa3\x95"
_FMT_TYPE_ID = 128  # 0x80

# ArduPilot DataFlash format codes to struct format strings
FORMAT_TO_STRUCT = {
    "b": ("b", int),
    "B": ("B", int),
    "h": ("h", int),
    "H": ("H", int),
    "i": ("i", int),
    "I": ("I", int),
    "q": ("q", int),
    "Q": ("Q", int),
    "f": ("f", float),
    "d": ("d", float),
    "n": ("4s", None),
    "N": ("16s", None),
    "Z": ("64s", None),
    "c": ("h", int),
    "C": ("H", int),
    "e": ("i", int),
    "E": ("I", int),
    "L": ("i", int),
    "M": ("b", int),
    "q": ("q", int),
}


@dataclass
class LogEntry:
    """A single parsed log entry."""

    msg_type: str
    timestamp_ms: float
    fields: dict = field(default_factory=dict)

    def __getattr__(self, name: str):
        if name in ("msg_type", "timestamp_ms", "fields"):
            return super().__getattribute__(name)
        try:
            return self.fields[name]
        except KeyError:
            raise AttributeError(
                f"'{self.msg_type}' message has no field '{name}'"
            ) from None


@dataclass
class _FmtDef:
    """Internal format definition for a message type."""

    type_id: int
    msg_type: str
    struct_fmt: str
    field_names: list[str]
    field_types: list[str]
    msg_length: int


class ArdupilotLog:
    """ArduPilot DataFlash .bin log reader.

    Can use pymavlink (if available) for robust parsing, or fall back
    to a standalone binary parser.

    Usage:
        log = ArdupilotLog("flight.bin")
        for entry in log.iter_messages("GPS", "BARO"):
            print(entry.msg_type, entry.fields)
    """

    def __init__(self, path: str | Path, *, use_pymavlink: Optional[bool] = None):
        self.path = Path(path)
        if not self.path.exists():
            raise FileNotFoundError(f"Log file not found: {self.path}")

        self._use_pymav = use_pymavlink
        self._fmt_defs: dict[str, _FmtDef] = {}
        self._gps_week: Optional[int] = None

    def iter_messages(
        self, *msg_types: str, include_all: bool = False
    ) -> Iterator[LogEntry]:
        """Iterate over log messages, optionally filtering by type.

        Args:
            *msg_types: Message types to include (e.g., "GPS", "BARO").
            include_all: If True, yield all messages (ignores msg_types filter).
        """
        if self._use_pymavlink is not False:
            try:
                yield from self._iter_pymavlink(msg_types, include_all)
                return
            except ImportError:
                if self._use_pymavlink is True:
                    raise

        yield from self._iter_standalone(msg_types, include_all)

    def _iter_pymavlink(
        self, msg_types: tuple[str, ...], include_all: bool
    ) -> Iterator[LogEntry]:
        """Parse using pymavlink's DFReader."""
        from pymavlink import mavutil

        log = mavutil.mavlink_connection(
            str(self.path), dialect="ardupilotmega", zero_time_base=True
        )

        filter_set = set(msg_types) if msg_types else None

        while True:
            m = log.recv_msg()
            if m is None:
                break

            mtype = m.get_type()
            if mtype in ("FMT", "FMTU", "MULT", "PARM", "MSG", "UNIT", "MODL"):
                continue

            if not include_all and filter_set and mtype not in filter_set:
                continue

            fields = {}
            for fname in m._fieldnames:
                if fname.startswith("_"):
                    continue
                fields[fname] = getattr(m, fname)

            # Extract timestamp
            ts = getattr(m, "TimeMS", None) or getattr(m, "TimeUS", None) or 0
            if hasattr(ts, "__float__"):
                ts = float(ts)
            else:
                ts = float(ts) if ts else 0.0

            # For usec timestamps, convert to ms
            if "TimeUS" in m._fieldnames and "TimeMS" not in m._fieldnames:
                ts = ts / 1000.0

            yield LogEntry(msg_type=mtype, timestamp_ms=ts, fields=fields)

    def _iter_standalone(
        self, msg_types: tuple[str, ...], include_all: bool
    ) -> Iterator[LogEntry]:
        """Parse using standalone binary parser (no dependencies)."""
        filter_set = set(msg_types) if msg_types else None

        with open(self.path, "rb") as f:
            data = f.read()

        pos = 0
        length = len(data)

        while pos < length - 3:
            # Find packet header
            if data[pos : pos + 2] != _PACKET_HEADER:
                pos += 1
                continue

            if pos + 3 > length:
                break

            msg_type_id = data[pos + 2]
            msg_start = pos + 3

            if msg_type_id == _FMT_TYPE_ID:
                # Parse FMT message
                consumed = self._parse_fmt(data, msg_start)
                if consumed < 0:
                    pos += 1
                    continue
                pos = msg_start + consumed
                continue

            # Look up format definition
            fmt_def = None
            for fd in self._fmt_defs.values():
                if fd.type_id == msg_type_id:
                    fmt_def = fd
                    break

            if fmt_def is None:
                pos += 1
                continue

            msg_len = fmt_def.msg_length
            if msg_start + msg_len > length:
                break

            msg_data = data[msg_start : msg_start + msg_len]

            try:
                values = struct.unpack(f"<{fmt_def.struct_fmt}", msg_data)
            except struct.error:
                pos = msg_start + msg_len
                continue

            fields = {}
            for name, val in zip(fmt_def.field_names, values):
                fields[name] = val

            # Compute timestamp
            ts = 0.0
            if "TimeMS" in fields:
                ts = float(fields["TimeMS"])
            elif "TimeUS" in fields:
                ts = float(fields["TimeUS"]) / 1000.0

            mtype = fmt_def.msg_type
            if not include_all and filter_set and mtype not in filter_set:
                pos = msg_start + msg_len
                continue

            yield LogEntry(msg_type=mtype, timestamp_ms=ts, fields=fields)
            pos = msg_start + msg_len

    def _parse_fmt(self, data: bytes, offset: int) -> int:
        """Parse a FMT message, return number of bytes consumed or -1 on error."""
        try:
            type_id = data[offset]
            msg_length = data[offset + 1]
            name_bytes = data[offset + 2 : offset + 6]
            format_bytes = data[offset + 6 : offset + 22]

            msg_type = name_bytes.split(b"\x00")[0].decode("ascii", errors="replace")
            fmt_str = format_bytes.split(b"\x00")[0].decode("ascii", errors="replace")

            # Find end of field names (null terminated)
            fields_start = offset + 22
            fields_end = data.index(b"\x00", fields_start) if b"\x00" in data[fields_start:] else len(data)
            field_names_str = data[fields_start:fields_end].decode("ascii", errors="replace")

            field_names = [n.strip() for n in field_names_str.split(",") if n.strip()]

            # Build struct format
            struct_parts = []
            converter_map = []
            for ch in fmt_str:
                if ch in FORMAT_TO_STRUCT:
                    sf, cvt = FORMAT_TO_STRUCT[ch]
                    struct_parts.append(sf)
                    converter_map.append(cvt)
                elif ch in ("A", "a"):
                    # 'A' is array, usually skipped in FMT
                    continue

            struct_fmt = "".join(struct_parts)

            # Calculate expected message length
            try:
                expected_len = struct.calcsize(f"<{struct_fmt}")
            except struct.error:
                return -1

            self._fmt_defs[msg_type] = _FmtDef(
                type_id=type_id,
                msg_type=msg_type,
                struct_fmt=struct_fmt,
                field_names=field_names,
                field_types=list(fmt_str),
                msg_length=expected_len,
            )

            return 3 + (fields_end - offset) + 1  # approximate consumed bytes

        except (IndexError, ValueError):
            return -1

    def get_gps_week(self) -> Optional[int]:
        """Extract GPS week number from the log."""
        for entry in self.iter_messages("GPS", "GPS2"):
            week = entry.fields.get("Week") or entry.fields.get("GWk")
            if week and week > 0:
                return int(week)
        return None

    def get_first_gps_time(self) -> Optional[tuple[int, float]]:
        """Get first GPS week and time-of-week in ms."""
        for entry in self.iter_messages("GPS", "GPS2"):
            week = entry.fields.get("Week") or entry.fields.get("GWk")
            time_ms = (
                entry.fields.get("TimeMS")
                or entry.fields.get("T")
                or entry.fields.get("GMS")
            )
            if week and time_ms and week > 0:
                return (int(week), float(time_ms))
        return None
