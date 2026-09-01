using System;
using System.Collections.Generic;
using System.IO;

namespace Ardu2IGC;

/// <summary>
/// Options for converting ArduPilot .bin logs to IGC format.
/// </summary>
public sealed class ConversionOptions
{
    public string PilotName { get; set; } = "";
    public string GliderType { get; set; } = "";
    public string GliderId { get; set; } = "";
    public string CompetitionId { get; set; } = "";
    public string CompetitionClass { get; set; } = "";

    /// <summary>Altitude source: "baro", "gps", or "both".</summary>
    public string AltitudeSource { get; set; } = "both";

    /// <summary>Maximum time gap between fixes in seconds before inserting a break.</summary>
    public double MaxTimeGap { get; set; } = 60.0;

    /// <summary>Override fix accuracy in meters. 0 = auto from GPS message.</summary>
    public int FixAccuracyOverride { get; set; } = 0;

    /// <summary>Use GPS2 message instead of GPS.</summary>
    public bool UseGps2 { get; set; } = false;
}

/// <summary>
/// Result of a conversion operation.
/// </summary>
public sealed class ConversionResult
{
    public string Input { get; init; } = "";
    public string Output { get; init; } = "";
    public int FixesWritten { get; init; }
    public int GapsSkipped { get; init; }
    public string FlightDate { get; init; } = "unknown";
}

/// <summary>
/// Converts ArduPilot .bin log files to IGC format.
/// </summary>
public static class ArdupilotConverter
{
    private static readonly DateTime GpsEpoch = new(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Convert an ArduPilot .bin log file to IGC format.
    /// </summary>
    public static ConversionResult Convert(
        string inputPath,
        string outputPath,
        ConversionOptions? options = null)
    {
        options ??= new ConversionOptions();

        var log = new ArdupilotLog(inputPath);
        var writer = new IgcWriter();

        // Set header
        if (!string.IsNullOrEmpty(options.PilotName))
            writer.Header.PilotName = options.PilotName;
        if (!string.IsNullOrEmpty(options.GliderType))
            writer.Header.GliderType = options.GliderType;
        if (!string.IsNullOrEmpty(options.GliderId))
            writer.Header.GliderId = options.GliderId;
        if (!string.IsNullOrEmpty(options.CompetitionId))
            writer.Header.CompetitionId = options.CompetitionId;
        if (!string.IsNullOrEmpty(options.CompetitionClass))
            writer.Header.CompetitionClass = options.CompetitionClass;

        // Collect barometric data
        var baroData = new SortedDictionary<double, int>();
        foreach (var entry in log.IterateMessages("BARO", "BAR2"))
        {
            if (entry.Fields.TryGetValue("Alt", out var altVal))
            {
                baroData[entry.TimestampMs] = System.Convert.ToInt32(altVal);
            }
            else if (entry.Fields.TryGetValue("AltChr", out var altChrVal))
            {
                baroData[entry.TimestampMs] = System.Convert.ToInt32(altChrVal);
            }
        }

        // Process GPS fixes
        string gpsType = options.UseGps2 ? "GPS2" : "GPS";
        int fixCount = 0;
        int skipCount = 0;
        double? lastGpsTime = null;
        DateTime? flightDate = null;

        foreach (var entry in log.IterateMessages(gpsType, "GPS"))
        {
            var gpsFix = ParseGpsMessage(entry);
            if (gpsFix is null) continue;

            // Extract flight date from GPS week
            if (flightDate is null)
            {
                var gpsTime = ExtractGpsTime(entry);
                if (gpsTime.HasValue)
                {
                    flightDate = GpsEpoch.AddDays(gpsTime.Value.Week * 7)
                        .AddMilliseconds(gpsTime.Value.TimeMs);
                }
            }

            // Check time gap
            double currentTime = gpsFix.Timestamp.Hour * 3600
                + gpsFix.Timestamp.Minute * 60
                + gpsFix.Timestamp.Second;

            if (lastGpsTime.HasValue)
            {
                double gap = currentTime - lastGpsTime.Value;
                if (gap < 0) gap += 86400; // Handle midnight crossing
                if (gap > options.MaxTimeGap)
                {
                    skipCount++;
                    lastGpsTime = currentTime;
                    continue;
                }
            }

            // Find nearest barometric reading
            int pressAlt = gpsFix.GpsAltitude;
            if (options.AltitudeSource is "baro" or "both")
            {
                double minDiff = double.MaxValue;
                int nearestBaro = 0;
                foreach (var kvp in baroData)
                {
                    double diff = Math.Abs(kvp.Key - entry.TimestampMs);
                    if (diff < minDiff)
                    {
                        minDiff = diff;
                        nearestBaro = kvp.Value;
                    }
                }
                if (minDiff < 5000)
                    pressAlt = nearestBaro;
            }

            int gpsAlt = options.AltitudeSource == "baro"
                ? pressAlt
                : gpsFix.GpsAltitude;

            if (options.AltitudeSource == "gps")
                pressAlt = gpsAlt;

            int fxa = options.FixAccuracyOverride > 0
                ? options.FixAccuracyOverride
                : gpsFix.FixAccuracy;

            writer.AddFix(new IgcFix
            {
                Timestamp = gpsFix.Timestamp,
                Latitude = gpsFix.Latitude,
                Longitude = gpsFix.Longitude,
                Valid = gpsFix.Valid,
                PressureAltitude = pressAlt,
                GpsAltitude = gpsAlt,
                FixAccuracy = fxa,
            });

            fixCount++;
            lastGpsTime = currentTime;
        }

        // Set flight date on header
        if (flightDate.HasValue)
            writer.Header.Date = flightDate.Value.Date;

        // Write output
        writer.Write(outputPath);

        return new ConversionResult
        {
            Input = inputPath,
            Output = outputPath,
            FixesWritten = fixCount,
            GapsSkipped = skipCount,
            FlightDate = flightDate?.ToString("yyyy-MM-dd") ?? "unknown",
        };
    }

    /// <summary>
    /// Get information about a .bin log file without converting.
    /// </summary>
    public static Dictionary<string, object> GetInfo(string inputPath)
    {
        var log = new ArdupilotLog(inputPath);
        var msgCounts = new Dictionary<string, int>();
        int baroCount = 0;
        int? gpsWeek = null;

        foreach (var entry in log.IterateMessages())
        {
            if (!msgCounts.ContainsKey(entry.MsgType))
                msgCounts[entry.MsgType] = 0;
            msgCounts[entry.MsgType]++;

            if (entry.MsgType is "GPS" or "GPS2" && gpsWeek is null)
            {
                if (entry.Fields.TryGetValue("Week", out var w) && System.Convert.ToInt32(w) > 0)
                    gpsWeek = System.Convert.ToInt32(w);
                else if (entry.Fields.TryGetValue("GWk", out var gwk) && System.Convert.ToInt32(gwk) > 0)
                    gpsWeek = System.Convert.ToInt32(gwk);
            }
        }

        foreach (var entry in log.IterateMessages("BARO", "BAR2"))
            baroCount++;

        int totalMsgs = 0;
        foreach (var c in msgCounts.Values) totalMsgs += c;

        return new Dictionary<string, object>
        {
            ["file"] = inputPath,
            ["messageTypes"] = msgCounts.Count,
            ["totalMessages"] = totalMsgs,
            ["hasGps"] = msgCounts.ContainsKey("GPS") || msgCounts.ContainsKey("GPS2"),
            ["hasBaro"] = baroCount > 0,
            ["baroReadings"] = baroCount,
            ["gpsWeek"] = gpsWeek ?? 0,
        };
    }

    private static IgcFix? ParseGpsMessage(LogEntry entry)
    {
        if (!entry.Fields.TryGetValue("Lat", out var latRaw) ||
            !entry.Fields.TryGetValue("Lng", out var lonRaw))
            return null;

        double lat = System.Convert.ToDouble(latRaw) / 1e7;
        double lon = System.Convert.ToDouble(lonRaw) / 1e7;
        int alt = entry.Fields.TryGetValue("Alt", out var altVal)
            ? System.Convert.ToInt32(altVal) : 0;

        int fixType = entry.Fields.TryGetValue("Status", out var status)
            ? System.Convert.ToInt32(status)
            : entry.Fields.TryGetValue("FixType", out var fixType2)
                ? System.Convert.ToInt32(fixType2) : 0;
        bool valid = fixType >= 2;

        // Time
        DateTime timestamp;
        if (entry.Fields.TryGetValue("TimeMS", out var timeMs))
        {
            double ms = System.Convert.ToDouble(timeMs);
            int totalSecs = (int)(ms / 1000) % 86400;
            timestamp = new DateTime(2000, 1, 1,
                totalSecs / 3600, (totalSecs % 3600) / 60, totalSecs % 60,
                DateTimeKind.Utc);
        }
        else
        {
            int totalSecs = (int)(entry.TimestampMs / 1000) % 86400;
            timestamp = new DateTime(2000, 1, 1,
                totalSecs / 3600, (totalSecs % 3600) / 60, totalSecs % 60,
                DateTimeKind.Utc);
        }

        // Fix accuracy from HDop
        int accuracy = 50;
        if (entry.Fields.TryGetValue("HDop", out var hdopVal))
        {
            double hdop = System.Convert.ToDouble(hdopVal);
            if (hdop > 0)
                accuracy = (int)(hdop * 5.0);
        }

        return new IgcFix
        {
            Timestamp = timestamp,
            Latitude = lat,
            Longitude = lon,
            Valid = valid,
            GpsAltitude = alt,
            FixAccuracy = accuracy,
        };
    }

    private static (int Week, double TimeMs)? ExtractGpsTime(LogEntry entry)
    {
        int week = 0;
        double timeMs = 0;

        if (entry.Fields.TryGetValue("Week", out var w))
            week = System.Convert.ToInt32(w);
        else if (entry.Fields.TryGetValue("GWk", out var gwk))
            week = System.Convert.ToInt32(gwk);

        if (entry.Fields.TryGetValue("TimeMS", out var tms))
            timeMs = System.Convert.ToDouble(tms);
        else if (entry.Fields.TryGetValue("T", out var t))
            timeMs = System.Convert.ToDouble(t);
        else if (entry.Fields.TryGetValue("GMS", out var gms))
            timeMs = System.Convert.ToDouble(gms);

        if (week > 0 && timeMs > 0)
            return (week, timeMs);
        return null;
    }
}
