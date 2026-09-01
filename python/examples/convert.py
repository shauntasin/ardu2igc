#!/usr/bin/env python3
"""CLI tool for converting ArduPilot .bin logs to IGC format.

Usage:
    python convert.py flight.bin
    python convert.py flight.bin -o output.igc --pilot "John Doe"
    python convert.py flight.bin --info
"""

import argparse
import json
import sys
from pathlib import Path

from ardu2igc import convert, ConversionOptions
from ardu2igc.converter import convert_info


def main():
    parser = argparse.ArgumentParser(
        description="Convert ArduPilot .bin log files to IGC format.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""\
Examples:
  %(prog)s flight.bin                    # Convert with defaults
  %(prog)s flight.bin -o myflight.igc    # Specify output file
  %(prog)s flight.bin --pilot "Jane Doe" # Set pilot name
  %(prog)s flight.bin --info             # Show log file info
  %(prog)s flight.bin --alt-source gps   # Use GPS altitude only
""",
    )

    parser.add_argument("input", help="Path to ArduPilot .bin log file")
    parser.add_argument(
        "-o", "--output", help="Output IGC file path (default: <input>.igc)"
    )
    parser.add_argument("--pilot", default="", help="Pilot name")
    parser.add_argument("--glider-type", default="", help="Glider/aircraft type")
    parser.add_argument("--glider-id", default="", help="Glider registration/ID")
    parser.add_argument("--comp-id", default="", help="Competition ID")
    parser.add_argument("--comp-class", default="", help="Competition class")
    parser.add_argument(
        "--alt-source",
        choices=["baro", "gps", "both"],
        default="both",
        help="Altitude source (default: both)",
    )
    parser.add_argument(
        "--max-gap",
        type=float,
        default=60.0,
        help="Max time gap between fixes in seconds (default: 60)",
    )
    parser.add_argument(
        "--fix-accuracy",
        type=int,
        default=0,
        help="Override fix accuracy in meters (default: auto)",
    )
    parser.add_argument(
        "--gps2", action="store_true", help="Use GPS2 message instead of GPS"
    )
    parser.add_argument(
        "--info", action="store_true", help="Show log file info without converting"
    )
    parser.add_argument(
        "--json", action="store_true", help="Output info as JSON (with --info)"
    )

    args = parser.parse_args()

    input_path = Path(args.input)
    if not input_path.exists():
        print(f"Error: File not found: {input_path}", file=sys.stderr)
        sys.exit(1)

    if args.info:
        info = convert_info(input_path)
        if args.json:
            print(json.dumps(info, indent=2))
        else:
            print(f"File: {info['file']}")
            print(f"Message types: {info['message_types']}")
            print(f"Total messages: {info['total_messages']}")
            print(f"Has GPS: {info['has_gps']}")
            print(f"Has BARO: {info['has_baro']} ({info['baro_readings']} readings)")
            if info["gps_info"]:
                week, tow = info["gps_info"]
                print(f"GPS week: {week}, time-of-week: {tow:.0f} ms")
            print()
            print("Message type counts:")
            for mtype, count in sorted(info["message_counts"].items()):
                print(f"  {mtype:12s} {count:>8d}")
        return

    output_path = Path(args.output) if args.output else input_path.with_suffix(".igc")

    options = ConversionOptions(
        pilot_name=args.pilot,
        glider_type=args.glider_type,
        glider_id=args.glider_id,
        competition_id=args.comp_id,
        competition_class=args.comp_class,
        altitude_source=args.alt_source,
        max_time_gap=args.max_gap,
        fix_accuracy_override=args.fix_accuracy,
        use_gps2=args.gps2,
    )

    print(f"Converting {input_path} to {output_path}...")
    result = convert(input_path, output_path, options)

    print(f"Done!")
    print(f"  Fixes written: {result['fixes_written']}")
    print(f"  Gaps skipped:  {result['gaps_skipped']}")
    print(f"  Flight date:   {result['flight_date']}")
    print(f"  Output:        {result['output']}")


if __name__ == "__main__":
    main()
