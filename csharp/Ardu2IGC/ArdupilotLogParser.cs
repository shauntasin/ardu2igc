using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ardu2IGC;

/// <summary>
/// Represents a single parsed log entry from an ArduPilot DataFlash .bin log.
/// </summary>
public sealed class LogEntry
{
    public string MsgType { get; init; } = "";
    public double TimestampMs { get; init; }
    public Dictionary<string, object> Fields { get; init; } = new();

    public T? Get<T>(string fieldName)
    {
        if (Fields.TryGetValue(fieldName, out var value))
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        return default;
    }
}

/// <summary>
/// Internal format definition for a message type, parsed from FMT messages.
/// </summary>
internal sealed class FmtDef
{
    public byte TypeId { get; init; }
    public string MsgType { get; init; } = "";
    public string StructFmt { get; init; } = "";
    public List<string> FieldNames { get; init; } = new();
    public int MsgLength { get; init; }
}

/// <summary>
/// ArduPilot DataFlash .bin log parser.
/// 
/// Parses the self-describing binary format used by ArduCopter and ArduPlane.
/// Message schemas are discovered from FMT messages within each file.
/// </summary>
public sealed class ArdupilotLog
{
    private static readonly byte[] PacketHeader = new byte[] { 0xa3, 0x95 };
    private const byte FmtTypeId = 128; // 0x80

    // ArduPilot DataFlash format codes to (struct format, field size)
    private static readonly Dictionary<char, (string StructFmt, int Size)> FormatToStruct = new()
    {
        ['b'] = ("b", 1),
        ['B'] = ("B", 1),
        ['h'] = ("h", 2),
        ['H'] = ("H", 2),
        ['i'] = ("i", 4),
        ['I'] = ("I", 4),
        ['q'] = ("q", 8),
        ['Q'] = ("Q", 8),
        ['f'] = ("f", 4),
        ['d'] = ("d", 8),
        ['n'] = ("4s", 4),
        ['N'] = ("16s", 16),
        ['Z'] = ("64s", 64),
        ['c'] = ("h", 2),
        ['C'] = ("H", 2),
        ['e'] = ("i", 4),
        ['E'] = ("I", 4),
        ['L'] = ("i", 4),
        ['M'] = ("b", 1),
    };

    private readonly byte[] _data;
    private readonly Dictionary<string, FmtDef> _fmtDefs = new();
    private readonly string _filePath;

    /// <summary>
    /// Open an ArduPilot .bin log file for parsing.
    /// </summary>
    public ArdupilotLog(string filePath)
    {
        _filePath = filePath;
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Log file not found: {filePath}");

        _data = File.ReadAllBytes(filePath);
        ParseFmtMessages();
    }

    /// <summary>
    /// Open an ArduPilot .bin log from a byte array.
    /// </summary>
    public ArdupilotLog(byte[] data)
    {
        _filePath = "<memory>";
        _data = data;
        ParseFmtMessages();
    }

    /// <summary>
    /// Iterate over all log entries, optionally filtering by message type.
    /// </summary>
    public IEnumerable<LogEntry> IterateMessages(params string[] msgTypes)
    {
        var filterSet = msgTypes.Length > 0 ? new HashSet<string>(msgTypes) : null;
        int pos = 0;
        int length = _data.Length;

        while (pos < length - 3)
        {
            // Find packet header
            if (_data[pos] != PacketHeader[0] || _data[pos + 1] != PacketHeader[1])
            {
                pos++;
                continue;
            }

            byte msgTypeId = _data[pos + 2];
            int msgStart = pos + 3;

            if (msgTypeId == FmtTypeId)
            {
                int consumed = ParseFmt(msgStart);
                pos = consumed > 0 ? msgStart + consumed : msgStart + 1;
                continue;
            }

            // Look up format definition
            FmtDef? fmtDef = null;
            foreach (var fd in _fmtDefs.Values)
            {
                if (fd.TypeId == msgTypeId)
                {
                    fmtDef = fd;
                    break;
                }
            }

            if (fmtDef is null)
            {
                pos++;
                continue;
            }

            int msgLen = fmtDef.MsgLength;
            if (msgStart + msgLen > length)
                break;

            var fields = ParseMessageFields(_data, msgStart, msgLen, fmtDef);

            // Compute timestamp
            double ts = 0.0;
            if (fields.TryGetValue("TimeMS", out var timeMsVal))
                ts = Convert.ToDouble(timeMsVal);
            else if (fields.TryGetValue("TimeUS", out var timeUsVal))
                ts = Convert.ToDouble(timeUsVal) / 1000.0;

            string mtype = fmtDef.MsgType;
            if (filterSet != null && !filterSet.Contains(mtype))
            {
                pos = msgStart + msgLen;
                continue;
            }

            yield return new LogEntry
            {
                MsgType = mtype,
                TimestampMs = ts,
                Fields = fields,
            };

            pos = msgStart + msgLen;
        }
    }

    /// <summary>
    /// Get the first GPS week number found in the log, or null if none.
    /// </summary>
    public int? GetGpsWeek()
    {
        foreach (var entry in IterateMessages("GPS", "GPS2"))
        {
            if (entry.Fields.TryGetValue("Week", out var week) && Convert.ToInt32(week) > 0)
                return Convert.ToInt32(week);
            if (entry.Fields.TryGetValue("GWk", out var gwk) && Convert.ToInt32(gwk) > 0)
                return Convert.ToInt32(gwk);
        }
        return null;
    }

    /// <summary>
    /// Get the first GPS time (week, time-of-week ms) found in the log.
    /// </summary>
    public (int Week, double TimeMs)? GetFirstGpsTime()
    {
        foreach (var entry in IterateMessages("GPS", "GPS2"))
        {
            int week = 0;
            double timeMs = 0;

            if (entry.Fields.TryGetValue("Week", out var w))
                week = Convert.ToInt32(w);
            else if (entry.Fields.TryGetValue("GWk", out var gwk))
                week = Convert.ToInt32(gwk);

            if (entry.Fields.TryGetValue("TimeMS", out var tms))
                timeMs = Convert.ToDouble(tms);
            else if (entry.Fields.TryGetValue("T", out var t))
                timeMs = Convert.ToDouble(t);
            else if (entry.Fields.TryGetValue("GMS", out var gms))
                timeMs = Convert.ToDouble(gms);

            if (week > 0 && timeMs > 0)
                return (week, timeMs);
        }
        return null;
    }

    private void ParseFmtMessages()
    {
        int pos = 0;
        int length = _data.Length;

        while (pos < length - 3)
        {
            if (_data[pos] != PacketHeader[0] || _data[pos + 1] != PacketHeader[1])
            {
                pos++;
                continue;
            }

            byte msgTypeId = _data[pos + 2];
            int msgStart = pos + 3;

            if (msgTypeId == FmtTypeId)
            {
                int consumed = ParseFmt(msgStart);
                pos = consumed > 0 ? msgStart + consumed : msgStart + 1;
                continue;
            }

            // Skip non-FMT messages during initial parse
            pos++;
        }
    }

    private int ParseFmt(int offset)
    {
        try
        {
            if (offset + 22 > _data.Length)
                return -1;

            byte typeId = _data[offset];
            byte msgLength = _data[offset + 1];

            // Extract message name (4 bytes, null-terminated)
            var nameBytes = new byte[4];
            Array.Copy(_data, offset + 2, nameBytes, 0, 4);
            int nameEnd = Array.IndexOf(nameBytes, (byte)0);
            string msgType = Encoding.ASCII.GetString(nameBytes, 0, nameEnd >= 0 ? nameEnd : 4);

            // Extract format string (16 bytes, null-terminated)
            var fmtBytes = new byte[16];
            Array.Copy(_data, offset + 6, fmtBytes, 0, 16);
            int fmtEnd = Array.IndexOf(fmtBytes, (byte)0);
            string fmtStr = Encoding.ASCII.GetString(fmtBytes, 0, fmtEnd >= 0 ? fmtEnd : 16);

            // Extract field names
            int fieldsStart = offset + 22;
            int fieldsEnd = Array.IndexOf(_data, (byte)0, fieldsStart);
            if (fieldsEnd < 0) fieldsEnd = Math.Min(fieldsStart + 200, _data.Length);
            string fieldNamesStr = Encoding.ASCII.GetString(_data, fieldsStart, fieldsEnd - fieldsStart);
            var fieldNames = new List<string>();
            foreach (var name in fieldNamesStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = name.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    fieldNames.Add(trimmed);
            }

            // Build struct format and calculate message length
            var structParts = new StringBuilder();
            int structLen = 0;
            foreach (char ch in fmtStr)
            {
                if (FormatToStruct.TryGetValue(ch, out var info))
                {
                    structParts.Append(info.StructFmt);
                    structLen += info.Size;
                }
            }

            _fmtDefs[msgType] = new FmtDef
            {
                TypeId = typeId,
                MsgType = msgType,
                StructFmt = structParts.ToString(),
                FieldNames = fieldNames,
                MsgLength = structLen,
            };

            return fieldsEnd - offset + 1;
        }
        catch
        {
            return -1;
        }
    }

    private static Dictionary<string, object> ParseMessageFields(
        byte[] data, int offset, int msgLen, FmtDef fmtDef)
    {
        var fields = new Dictionary<string, object>();
        int pos = offset;
        int fieldIndex = 0;

        foreach (char ch in fmtDef.StructFmt)
        {
            if (fieldIndex >= fmtDef.FieldNames.Count)
                break;

            string fieldName = fmtDef.FieldNames[fieldIndex];
            fieldIndex++;

            switch (ch)
            {
                case 'b': // int8
                    if (pos + 1 <= offset + msgLen)
                    {
                        fields[fieldName] = (sbyte)data[pos];
                        pos += 1;
                    }
                    break;
                case 'B': // uint8
                    if (pos + 1 <= offset + msgLen)
                    {
                        fields[fieldName] = data[pos];
                        pos += 1;
                    }
                    break;
                case 'h': // int16
                    if (pos + 2 <= offset + msgLen)
                    {
                        fields[fieldName] = BitConverter.ToInt16(data, pos);
                        pos += 2;
                    }
                    break;
                case 'H': // uint16
                    if (pos + 2 <= offset + msgLen)
                    {
                        fields[fieldName] = BitConverter.ToUInt16(data, pos);
                        pos += 2;
                    }
                    break;
                case 'i': // int32
                    if (pos + 4 <= offset + msgLen)
                    {
                        fields[fieldName] = BitConverter.ToInt32(data, pos);
                        pos += 4;
                    }
                    break;
                case 'I': // uint32
                    if (pos + 4 <= offset + msgLen)
                    {
                        fields[fieldName] = BitConverter.ToUInt32(data, pos);
                        pos += 4;
                    }
                    break;
                case 'q': // int64
                    if (pos + 8 <= offset + msgLen)
                    {
                        fields[fieldName] = BitConverter.ToInt64(data, pos);
                        pos += 8;
                    }
                    break;
                case 'Q': // uint64
                    if (pos + 8 <= offset + msgLen)
                    {
                        fields[fieldName] = BitConverter.ToUInt64(data, pos);
                        pos += 8;
                    }
                    break;
                case 'f': // float
                    if (pos + 4 <= offset + msgLen)
                    {
                        fields[fieldName] = BitConverter.ToSingle(data, pos);
                        pos += 4;
                    }
                    break;
                case 'd': // double
                    if (pos + 8 <= offset + msgLen)
                    {
                        fields[fieldName] = BitConverter.ToDouble(data, pos);
                        pos += 8;
                    }
                    break;
                case 'n': // char[4]
                    if (pos + 4 <= offset + msgLen)
                    {
                        fields[fieldName] = Encoding.ASCII.GetString(data, pos, 4).TrimEnd('\0');
                        pos += 4;
                    }
                    break;
                case 'N': // char[16]
                    if (pos + 16 <= offset + msgLen)
                    {
                        fields[fieldName] = Encoding.ASCII.GetString(data, pos, 16).TrimEnd('\0');
                        pos += 16;
                    }
                    break;
                case 'Z': // char[64]
                    if (pos + 64 <= offset + msgLen)
                    {
                        fields[fieldName] = Encoding.ASCII.GetString(data, pos, 64).TrimEnd('\0');
                        pos += 64;
                    }
                    break;
                default:
                    break;
            }
        }

        return fields;
    }
}
