using System;
using System.Text.Json;
using Ardu2IGC;

namespace Examples;

/// <summary>
/// CLI tool for converting ArduPilot .bin logs to IGC format.
///
/// Usage:
///   dotnet run --project Examples.csproj -- flight.bin
///   dotnet run --project Examples.csproj -- flight.bin -o output.igc --pilot "John Doe"
///   dotnet run --project Examples.csproj -- flight.bin --info
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return;
        }

        string? inputFile = null;
        string? outputFile = null;
        string pilotName = "";
        string gliderType = "";
        string gliderId = "";
        string compId = "";
        string compClass = "";
        string altSource = "both";
        double maxGap = 60.0;
        int fixAccuracy = 0;
        bool useGps2 = false;
        bool showInfo = false;
        bool outputJson = false;

        // Parse arguments
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o":
                case "--output":
                    if (i + 1 < args.Length) outputFile = args[++i];
                    break;
                case "--pilot":
                    if (i + 1 < args.Length) pilotName = args[++i];
                    break;
                case "--glider-type":
                    if (i + 1 < args.Length) gliderType = args[++i];
                    break;
                case "--glider-id":
                    if (i + 1 < args.Length) gliderId = args[++i];
                    break;
                case "--comp-id":
                    if (i + 1 < args.Length) compId = args[++i];
                    break;
                case "--comp-class":
                    if (i + 1 < args.Length) compClass = args[++i];
                    break;
                case "--alt-source":
                    if (i + 1 < args.Length) altSource = args[++i];
                    break;
                case "--max-gap":
                    if (i + 1 < args.Length) maxGap = double.Parse(args[++i]);
                    break;
                case "--fix-accuracy":
                    if (i + 1 < args.Length) fixAccuracy = int.Parse(args[++i]);
                    break;
                case "--gps2":
                    useGps2 = true;
                    break;
                case "--info":
                    showInfo = true;
                    break;
                case "--json":
                    outputJson = true;
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return;
                default:
                    if (!args[i].StartsWith('-'))
                        inputFile = args[i];
                    break;
            }
        }

        if (inputFile is null)
        {
            Console.Error.WriteLine("Error: No input file specified.");
            PrintUsage();
            Environment.Exit(1);
        }

        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"Error: File not found: {inputFile}");
            Environment.Exit(1);
        }

        if (showInfo)
        {
            ShowInfo(inputFile, outputJson);
            return;
        }

        outputFile ??= Path.ChangeExtension(inputFile, ".igc");

        var options = new ConversionOptions
        {
            PilotName = pilotName,
            GliderType = gliderType,
            GliderId = gliderId,
            CompetitionId = compId,
            CompetitionClass = compClass,
            AltitudeSource = altSource,
            MaxTimeGap = maxGap,
            FixAccuracyOverride = fixAccuracy,
            UseGps2 = useGps2,
        };

        Console.WriteLine($"Converting {inputFile} to {outputFile}...");

        var result = ArdupilotConverter.Convert(inputFile, outputFile, options);

        Console.WriteLine("Done!");
        Console.WriteLine($"  Fixes written: {result.FixesWritten}");
        Console.WriteLine($"  Gaps skipped:  {result.GapsSkipped}");
        Console.WriteLine($"  Flight date:   {result.FlightDate}");
        Console.WriteLine($"  Output:        {result.Output}");
    }

    static void ShowInfo(string inputFile, bool json)
    {
        var info = ArdupilotConverter.GetInfo(inputFile);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"File: {info["file"]}");
            Console.WriteLine($"Message types: {info["messageTypes"]}");
            Console.WriteLine($"Total messages: {info["totalMessages"]}");
            Console.WriteLine($"Has GPS: {info["hasGps"]}");
            Console.WriteLine($"Has BARO: {info["hasBaro"]} ({info["baroReadings"]} readings)");
            if (info["gpsWeek"] is int week)
                Console.WriteLine($"GPS week: {week}");
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("ardu2igc - Convert ArduPilot .bin logs to IGC format");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  ardu2igc <input.bin> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -o, --output <file>      Output IGC file path (default: <input>.igc)");
        Console.WriteLine("  --pilot <name>           Pilot name");
        Console.WriteLine("  --glider-type <type>     Glider/aircraft type");
        Console.WriteLine("  --glider-id <id>         Glider registration/ID");
        Console.WriteLine("  --comp-id <id>           Competition ID");
        Console.WriteLine("  --comp-class <class>     Competition class");
        Console.WriteLine("  --alt-source <src>       Altitude source: baro, gps, both (default: both)");
        Console.WriteLine("  --max-gap <seconds>      Max time gap between fixes (default: 60)");
        Console.WriteLine("  --fix-accuracy <meters>  Override fix accuracy (default: auto)");
        Console.WriteLine("  --gps2                   Use GPS2 message instead of GPS");
        Console.WriteLine("  --info                   Show log file info without converting");
        Console.WriteLine("  --json                   Output info as JSON (with --info)");
        Console.WriteLine("  -h, --help               Show this help message");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  ardu2igc flight.bin");
        Console.WriteLine("  ardu2igc flight.bin -o myflight.igc --pilot \"Jane Doe\"");
        Console.WriteLine("  ardu2igc flight.bin --info --json");
    }
}
