using System;
using System.IO;
using System.Text;

namespace Ardu2IGC;

/// <summary>
/// A single GPS fix in IGC format.
/// </summary>
public sealed class IgcFix
{
    /// <summary>UTC timestamp (HH:MM:SS).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Latitude in decimal degrees (positive = N, negative = S).</summary>
    public double Latitude { get; set; }

    /// <summary>Longitude in decimal degrees (positive = E, negative = W).</summary>
    public double Longitude { get; set; }

    /// <summary>True if 3D fix (A), false if 2D/invalid (V).</summary>
    public bool Valid { get; set; } = true;

    /// <summary>Pressure altitude in meters.</summary>
    public int PressureAltitude { get; set; }

    /// <summary>GNSS altitude in meters.</summary>
    public int GpsAltitude { get; set; }

    /// <summary>Fix accuracy (estimated position error) in meters.</summary>
    public int FixAccuracy { get; set; } = 50;
}

/// <summary>
/// IGC file header metadata.
/// </summary>
public sealed class IgcHeader
{
    public DateTime? Date { get; set; }
    public string PilotName { get; set; } = "";
    public string GliderType { get; set; } = "";
    public string GliderId { get; set; } = "";
    public string CompetitionId { get; set; } = "";
    public string CompetitionClass { get; set; } = "";
    public string FirmwareVersion { get; set; } = "ardu2igc 1.0";
    public string HardwareVersion { get; set; } = "ArduPilot";
    public string FrType { get; set; } = "ardu2igc,ArduPilot Converter";
    public string GpsReceiver { get; set; } = "ArduPilot,Internal,16ch,10000m";
    public string PressureSensor { get; set; } = "ArduPilot,Internal,11000m";
    public int FixAccuracy { get; set; } = 50;
    public string ManufacturerCode { get; set; } = "XXX";
    public string SerialNumber { get; set; } = "A2G";
}

/// <summary>
/// Writes IGC format files per FAI/IGC specification.
/// 
/// Usage:
///   var writer = new IgcWriter();
///   writer.Header.PilotName = "John Doe";
///   writer.AddFix(fix);
///   writer.Write("output.igc");
/// </summary>
public sealed class IgcWriter
{
    public IgcHeader Header { get; } = new();
    private readonly List<IgcFix> _fixes = new();

    /// <summary>Add a GPS fix to the file.</summary>
    public void AddFix(IgcFix fix) => _fixes.Add(fix);

    /// <summary>Add multiple GPS fixes.</summary>
    public void AddFixes(IEnumerable<IgcFix> fixes) => _fixes.AddRange(fixes);

    /// <summary>Write the IGC file to disk.</summary>
    public void Write(string filePath)
    {
        using var writer = new StreamWriter(filePath, false, new UTF8Encoding(false))
        {
            NewLine = "\r\n",
        };

        WriteARecord(writer);
        WriteHRecords(writer);
        WriteIRecord(writer);
        WriteBRecords(writer);
    }

    /// <summary>Generate the IGC file content as a string.</summary>
    public string Tostring()
    {
        var sb = new StringBuilder();
        var entries = new List<string>();

        entries.Add(BuildARecord());
        entries.AddRange(BuildHRecords());
        entries.Add("I013638FXA");
        foreach (var fix in _fixes)
        {
            entries.Add(BuildBRecord(fix));
        }

        return string.Join("\r\n", entries) + "\r\n";
    }

    private void WriteARecord(StreamWriter writer)
    {
        string mfr = Header.ManufacturerCode.Length >= 3
            ? Header.ManufacturerCode.Substring(0, 3)
            : Header.ManufacturerCode.PadRight(3);
        string sn = Header.SerialNumber.Length >= 3
            ? Header.SerialNumber.Substring(0, 3)
            : Header.SerialNumber.PadRight(3);
        writer.WriteLine($"A{mfr}{sn}ardu2igc Flight Recorder");
    }

    private void WriteHRecords(StreamWriter writer)
    {
        var h = Header;
        var date = h.Date ?? DateTime.UtcNow;

        writer.WriteLine($"HFDTE{date:ddMMyy}");
        writer.WriteLine($"HFFXA{h.FixAccuracy:000}");

        if (!string.IsNullOrEmpty(h.PilotName))
            writer.WriteLine($"HFPLTPILOTINCHARGE:{h.PilotName}");

        if (!string.IsNullOrEmpty(h.GliderType))
            writer.WriteLine($"HFGTYGLIDERTYPE:{h.GliderType}");

        if (!string.IsNullOrEmpty(h.GliderId))
            writer.WriteLine($"HFGIDGLIDERID:{h.GliderId}");

        if (!string.IsNullOrEmpty(h.CompetitionId))
            writer.WriteLine($"HFCIDCOMPETITIONID:{h.CompetitionId}");

        if (!string.IsNullOrEmpty(h.CompetitionClass))
            writer.WriteLine($"HFCCLCOMPETITIONCLASS:{h.CompetitionClass}");

        writer.WriteLine("HFDTM100GPSDATUM: WGS-1984");
        writer.WriteLine($"HFRFWFIRMWAREVERSION:{h.FirmwareVersion}");
        writer.WriteLine($"HFRHWHARDWAREVERSION:{h.HardwareVersion}");
        writer.WriteLine($"HFFTYFRTYPE:{h.FrType}");
        writer.WriteLine($"HFGPS{h.GpsReceiver}");
        writer.WriteLine($"HFPRS{h.PressureSensor}");
    }

    private void WriteIRecord(StreamWriter writer)
    {
        writer.WriteLine("I013638FXA");
    }

    private void WriteBRecords(StreamWriter writer)
    {
        foreach (var fix in _fixes)
        {
            writer.WriteLine(BuildBRecord(fix));
        }
    }

    private string BuildARecord()
    {
        string mfr = Header.ManufacturerCode.Length >= 3
            ? Header.ManufacturerCode.Substring(0, 3)
            : Header.ManufacturerCode.PadRight(3);
        string sn = Header.SerialNumber.Length >= 3
            ? Header.SerialNumber.Substring(0, 3)
            : Header.SerialNumber.PadRight(3);
        return $"A{mfr}{sn}ardu2igc Flight Recorder";
    }

    private List<string> BuildHRecords()
    {
        var h = Header;
        var date = h.Date ?? DateTime.UtcNow;
        var lines = new List<string>
        {
            $"HFDTE{date:ddMMyy}",
            $"HFFXA{h.FixAccuracy:000}",
        };

        if (!string.IsNullOrEmpty(h.PilotName))
            lines.Add($"HFPLTPILOTINCHARGE:{h.PilotName}");
        if (!string.IsNullOrEmpty(h.GliderType))
            lines.Add($"HFGTYGLIDERTYPE:{h.GliderType}");
        if (!string.IsNullOrEmpty(h.GliderId))
            lines.Add($"HFGIDGLIDERID:{h.GliderId}");
        if (!string.IsNullOrEmpty(h.CompetitionId))
            lines.Add($"HFCIDCOMPETITIONID:{h.CompetitionId}");
        if (!string.IsNullOrEmpty(h.CompetitionClass))
            lines.Add($"HFCCLCOMPETITIONCLASS:{h.CompetitionClass}");

        lines.Add("HFDTM100GPSDATUM: WGS-1984");
        lines.Add($"HFRFWFIRMWAREVERSION:{h.FirmwareVersion}");
        lines.Add($"HFRHWHARDWAREVERSION:{h.HardwareVersion}");
        lines.Add($"HFFTYFRTYPE:{h.FrType}");
        lines.Add($"HFGPS{h.GpsReceiver}");
        lines.Add($"HFPRS{h.PressureSensor}");

        return lines;
    }

    private static string BuildBRecord(IgcFix fix)
    {
        // Time: HHMMSS
        string timeStr = fix.Timestamp.ToString("HHmmss");

        // Latitude: DDMMmmmN/S
        double lat = Math.Abs(fix.Latitude);
        char latDir = fix.Latitude >= 0 ? 'N' : 'S';
        int latDeg = (int)lat;
        double latMinFull = (lat - latDeg) * 60;
        int latMinInt = (int)latMinFull;
        int latMinFrac = (int)Math.Round((latMinFull - latMinInt) * 1000);
        string latStr = $"{latDeg:00}{latMinInt:00}{latMinFrac:000}{latDir}";

        // Longitude: DDDMMmmmE/W
        double lon = Math.Abs(fix.Longitude);
        char lonDir = fix.Longitude >= 0 ? 'E' : 'W';
        int lonDeg = (int)lon;
        double lonMinFull = (lon - lonDeg) * 60;
        int lonMinInt = (int)lonMinFull;
        int lonMinFrac = (int)Math.Round((lonMinFull - lonMinInt) * 1000);
        string lonStr = $"{lonDeg:000}{lonMinInt:00}{lonMinFrac:000}{lonDir}";

        // Validity
        char validity = fix.Valid ? 'A' : 'V';

        // Pressure altitude (5 digits)
        int pressAlt = fix.PressureAltitude < -9999 ? -9999 : (fix.PressureAltitude > 99999 ? 99999 : fix.PressureAltitude);
        string pressAltStr = pressAlt >= 0
            ? $"{pressAlt:00000}"
            : $"-{Math.Abs(pressAlt):0000}";

        // GPS altitude (5 digits)
        int gpsAlt = fix.GpsAltitude < 0 ? 0 : (fix.GpsAltitude > 99999 ? 99999 : fix.GpsAltitude);
        string gpsAltStr = $"{gpsAlt:00000}";

        // Fix accuracy (3 digits)
        int fxa = fix.FixAccuracy < 0 ? 0 : (fix.FixAccuracy > 999 ? 999 : fix.FixAccuracy);
        string fxaStr = $"{fxa:000}";

        return $"B{timeStr}{latStr}{lonStr}{validity}{pressAltStr}{gpsAltStr}{fxaStr}";
    }
}
