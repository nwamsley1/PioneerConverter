// All using statements must come first
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;

using ThermoFisher.CommonCore.BackgroundSubtraction;
using ThermoFisher.CommonCore.Data;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoFisher.CommonCore.MassPrecisionEstimator;
using ThermoFisher.CommonCore.RawFileReader;

using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Memory;
using Apache.Arrow.Types;

// Then class declarations
internal static class AppMetadata
{
    public const string AppName = "PioneerConverter";
    private const string DefaultVersion = "0.0.0-dev";
    private static readonly Lazy<string> VersionProvider = new Lazy<string>(ResolveVersion);

    public static string Version => VersionProvider.Value;

    private static string ResolveVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return NormalizeVersion(informationalVersion);
        }

        string? assemblyVersion = assembly.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(assemblyVersion) ? DefaultVersion : NormalizeVersion(assemblyVersion);
    }

    private static string NormalizeVersion(string value)
    {
        int metadataSeparatorIndex = value.IndexOf('+');
        return metadataSeparatorIndex >= 0 ? value.Substring(0, metadataSeparatorIndex) : value;
    }
}

public class Options
{
    public string RawPath { get; set; } = string.Empty;
    public string OutputDir { get; set; } = string.Empty;
    public bool SkipExisting { get; set; } = false;
    public bool ShouldShowHelp { get; set; } = false;
    public bool ShouldShowVersion { get; set; } = false;
    public int BatchSize { get; set; } = 10000;
    public int ConcurrentFiles { get; set; } = 2;
    public int ThreadsPerFile { get; set; } = 3;
    public int ScanChunkSize { get; set; } = 128;
	
    public static Options ParseArguments(string[] args)
    {
        var options = new Options();
        
        if (args.Length == 0)
        {
            options.ShouldShowHelp = true;
            return options;
        }

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-b":
                case "--batch-size":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int batchSize))
                    {
                        options.BatchSize = batchSize;
                    }
                    else
                    {
                        Console.WriteLine("Invalid value for {0}", args[i]);
                        options.ShouldShowHelp = true;
                        return options;
                    }
                    break;
                case "-o":
                case "--output-dir":
                    if (i + 1 < args.Length)
                    {
                        options.OutputDir = args[++i];
                    }
                    else
                    {
                        Console.WriteLine("Missing value for {0}", args[i]);
                        options.ShouldShowHelp = true;
                        return options;
                    }
                    break;
                case "--skip-existing":
                    options.SkipExisting = true;
                    break;
                case "-n":
                case "--concurrent-files":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int concurrentFiles))
                    {
                        options.ConcurrentFiles = concurrentFiles;
                    }
                    else
                    {
                        Console.WriteLine("Invalid value for {0}", args[i]);
                        options.ShouldShowHelp = true;
                        return options;
                    }
                    break;
                case "-t":
                case "--threads-per-file":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int threadsPerFile))
                    {
                        options.ThreadsPerFile = threadsPerFile;
                    }
                    else
                    {
                        Console.WriteLine("Invalid value for {0}", args[i]);
                        options.ShouldShowHelp = true;
                        return options;
                    }
                    break;
                case "--scan-chunk-size":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int scanChunkSize))
                    {
                        options.ScanChunkSize = scanChunkSize;
                    }
                    else
                    {
                        Console.WriteLine("Invalid value for {0}", args[i]);
                        options.ShouldShowHelp = true;
                        return options;
                    }
                    break;
                case "--version":
                    options.ShouldShowVersion = true;
                    break;
                case "-h":
                case "--help":
                    options.ShouldShowHelp = true;
                    break;
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        Console.WriteLine("Unknown option: {0}", args[i]);
                        options.ShouldShowHelp = true;
                        return options;
                    }

                    if (string.IsNullOrEmpty(options.RawPath))
                    {
                        options.RawPath = args[i];
                    }
                    else
                    {
                        Console.WriteLine("Unexpected argument: {0}", args[i]);
                        options.ShouldShowHelp = true;
                        return options;
                    }
                    break;
            }
        }
	
        return options;
    }
	
    public static void ShowHelp()
    {
        Console.WriteLine($"{AppMetadata.AppName} {AppMetadata.Version}");
        Console.WriteLine();
        Console.WriteLine($"Usage: {AppMetadata.AppName} RAW_PATH [options]");
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  RAW_PATH                   Path to .raw file or directory containing .raw files");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -b, --batch-size <size>    Process this many scans in each batch (default: 10000)");
        Console.WriteLine("  -o, --output-dir <path>    Output directory for .arrow files (default: <input_dir>/arrow_out)");
        Console.WriteLine("      --skip-existing        Skip conversion when existing output appears complete");
        Console.WriteLine("  -n, --concurrent-files <n> Number of files to convert at the same time (default: 2)");
        Console.WriteLine("  -t, --threads-per-file <n> Scan extraction threads used for each file (default: 3)");
        Console.WriteLine("      --scan-chunk-size <n>  Scan chunk size for scan-thread mode (default: 128)");
        Console.WriteLine("      --version              Show version information");
        Console.WriteLine("  -h, --help                 Show help information");
    }
}

internal static class Program
{
    private const string HcdEnergyTrailerLabel = "HCD Energy V:";
    private const string ScanNumberColumnName = "scanNumber";

    public static void Main(string[] args)
    {
        var options = Options.ParseArguments(args);

        if (options.ShouldShowVersion)
        {
            Console.WriteLine($"{AppMetadata.AppName} {AppMetadata.Version}");
            return;
        }

        if (options.ShouldShowHelp)
        {
            Options.ShowHelp();
            return;
        }
	
        if (string.IsNullOrEmpty(options.RawPath))
        {
            Console.WriteLine("Missing required RAW_PATH argument.");
            Options.ShowHelp();
            return;
        }

        options.BatchSize = Math.Max(1, options.BatchSize);
        options.ConcurrentFiles = Math.Max(1, options.ConcurrentFiles);
        options.ThreadsPerFile = Math.Max(1, options.ThreadsPerFile);
        options.ScanChunkSize = Math.Max(1, options.ScanChunkSize);
	
        var totalExecutionWatch = Stopwatch.StartNew();

        bool rawPathIsFile = File.Exists(options.RawPath);
        bool rawPathIsDirectory = Directory.Exists(options.RawPath);
        if (!rawPathIsFile && !rawPathIsDirectory)
        {
            Console.WriteLine($"{AppMetadata.AppName} {AppMetadata.Version}");
            Console.WriteLine("File or Directory does not exist: {0}", options.RawPath);
            return;
        }

        string inputMode = rawPathIsDirectory ? "directory" : "file";
        string input_dir;
        if (rawPathIsDirectory)
        {
            input_dir = Path.GetFullPath(options.RawPath);
        }
        else
        {
            string inputFilePath = Path.GetFullPath(options.RawPath);
            string? inputFileDirectory = Path.GetDirectoryName(inputFilePath);
            if (inputFileDirectory == null)
            {
                Console.WriteLine($"{AppMetadata.AppName} {AppMetadata.Version}");
                Console.WriteLine("Invalid input directory");
                return;
            }

            input_dir = inputFileDirectory;
        }

        string[] file_paths = GetFilePaths(options.RawPath);
        string output_dir = buildOutputDir(input_dir, options.OutputDir);
        if (string.IsNullOrEmpty(output_dir))
        {
            return;
        }

        string[] output_paths = getOutputPaths(output_dir, file_paths);
        List<int> filesToConvert = new List<int>(file_paths.Length);
        int skippedCompleteFiles = 0;
        int reconvertedIncompleteFiles = 0;
        int missingOutputFiles = 0;
        for (int i = 0; i < file_paths.Length; i++)
        {
            if (!options.SkipExisting)
            {
                filesToConvert.Add(i);
                continue;
            }

            if (!File.Exists(output_paths[i]))
            {
                missingOutputFiles++;
                filesToConvert.Add(i);
                continue;
            }

            if (HasCompleteExistingOutput(file_paths[i], output_paths[i]))
            {
                skippedCompleteFiles++;
                continue;
            }

            reconvertedIncompleteFiles++;
            filesToConvert.Add(i);
        }
	
        ParallelOptions parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.ConcurrentFiles
        };

        Console.WriteLine($"{AppMetadata.AppName} {AppMetadata.Version}");
        Console.WriteLine("==================================================");
        Console.WriteLine($"Config: concurrent-files={options.ConcurrentFiles}  threads-per-file={options.ThreadsPerFile}  scan-chunk-size={options.ScanChunkSize}  batch-size={options.BatchSize}");
        Console.WriteLine($"Config: output={output_dir}");
        Console.WriteLine($"Config: skip-existing={options.SkipExisting.ToString().ToLowerInvariant()}");
        Console.WriteLine();
        Console.WriteLine($"Input : {inputMode} {options.RawPath}");
        Console.WriteLine($"Queue : discovered={file_paths.Length}  convert={filesToConvert.Count}");
        if (options.SkipExisting)
        {
            Console.WriteLine($"Queue : skipped-complete={skippedCompleteFiles}  reconvert-incomplete={reconvertedIncompleteFiles}  missing-output={missingOutputFiles}");
        }
        Console.WriteLine("==================================================");

        if (file_paths.Length == 0)
        {
            totalExecutionWatch.Stop();
            Console.WriteLine("No .raw files found to process");
            Console.WriteLine("Total conversion time: {0}", FormatDuration(totalExecutionWatch.Elapsed));
            return;
        }

        if (filesToConvert.Count == 0)
        {
            totalExecutionWatch.Stop();
            Console.WriteLine("No files to convert.");
            Console.WriteLine("Total conversion time: {0}", FormatDuration(totalExecutionWatch.Elapsed));
            return;
        }

        Parallel.ForEach(filesToConvert, parallelOptions, fileIndex =>
        {
            ProcessFile(file_paths[fileIndex], output_paths[fileIndex], options.BatchSize, options.ThreadsPerFile, options.ScanChunkSize);
        });

        totalExecutionWatch.Stop();
        Console.WriteLine("Total conversion time: {0}", FormatDuration(totalExecutionWatch.Elapsed));
    }

    public static string[] GetFilePaths(string raw_path)
    {
        //Initialize File Paths
        string[] file_paths;

        if (File.Exists(raw_path)) //Individual .raw file 
        {
            file_paths = new string[] { Path.GetFullPath(raw_path) };
        } else if (Directory.Exists(raw_path)) //All .raw files in a directory
        {   
            string directory_path = Path.GetFullPath(raw_path);
            file_paths = Directory.GetFiles(directory_path, "*.raw", SearchOption.TopDirectoryOnly);
        } else
        {
            file_paths = new string[0];
        }
        return file_paths;
    }

    public static string buildOutputDir(string input_dir, string requestedOutputDir)
    {
        string output_dir = string.IsNullOrWhiteSpace(requestedOutputDir)
            ? Path.Combine(input_dir, "arrow_out")
            : Path.GetFullPath(requestedOutputDir);

        if (File.Exists(output_dir))
        {
            Console.WriteLine("Output path points to an existing file: {0}", output_dir);
            return string.Empty;
        }

        try
        {
            Directory.CreateDirectory(output_dir);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Could not create output directory '{0}': {1}", output_dir, ex.Message);
            return string.Empty;
        }

        return output_dir;
    }

    private static bool HasCompleteExistingOutput(string inputFile, string outputFile)
    {
        try
        {
            int rawLastScan = GetRawLastScanNumber(inputFile);
            int? outputLastScan = GetOutputLastScanNumber(outputFile);
            return outputLastScan.HasValue && outputLastScan.Value == rawLastScan;
        }
        catch
        {
            return false;
        }
    }

    private static int GetRawLastScanNumber(string inputFile)
    {
        using var rawFile = RawFileReaderAdapter.FileFactory(inputFile);
        if (!rawFile.IsOpen || rawFile.IsError)
        {
            throw new InvalidOperationException($"Unable to read RAW file: {inputFile}");
        }

        rawFile.SelectInstrument(Device.MS, 1);
        return rawFile.RunHeaderEx.LastSpectrum;
    }

    private static int? GetOutputLastScanNumber(string outputFile)
    {
        using var fileStream = new FileStream(outputFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new ArrowFileReader(fileStream);

        int? lastScanNumber = null;
        int scanNumberFieldIndex = -1;
        while (reader.ReadNextRecordBatch() is RecordBatch batch)
        {
            using (batch)
            {
                if (batch.Length == 0)
                {
                    continue;
                }

                if (scanNumberFieldIndex < 0)
                {
                    for (int i = 0; i < batch.Schema.FieldsList.Count; i++)
                    {
                        if (string.Equals(batch.Schema.FieldsList[i].Name, ScanNumberColumnName, StringComparison.Ordinal))
                        {
                            scanNumberFieldIndex = i;
                            break;
                        }
                    }

                    if (scanNumberFieldIndex < 0)
                    {
                        throw new InvalidDataException($"Missing required column '{ScanNumberColumnName}' in output file: {outputFile}");
                    }
                }

                if (batch.Column(scanNumberFieldIndex) is not Int32Array scanNumbers)
                {
                    throw new InvalidDataException($"Column '{ScanNumberColumnName}' is not Int32 in output file: {outputFile}");
                }

                int lastIndex = checked((int)batch.Length - 1);
                int? batchLastScanNumber = scanNumbers.GetValue(lastIndex);
                if (!batchLastScanNumber.HasValue)
                {
                    throw new InvalidDataException($"Column '{ScanNumberColumnName}' has null values in output file: {outputFile}");
                }

                lastScanNumber = batchLastScanNumber.Value;
            }
        }

        return lastScanNumber;
    }
	
    public static string[] getOutputPaths(string output_dir, string[] file_paths)
    {
        //Make output paths by altering the file extension and directory 
        string[] output_paths = new string[file_paths.Length];
        for (var i = 0; i < file_paths.Length; i += 1) {
            string file_basename = Path.GetFileNameWithoutExtension(file_paths[i]);
            file_basename += ".arrow";
            output_paths[i] = Path.Combine(output_dir, file_basename);
        }
        return output_paths;
    }
    static void ProcessFile(string inputFile, string outputFile, int batchSize, int scanThreads, int scanChunkSize)
    {
        //var myThreadManager = RawFileReaderFactory.CreateThreadManager("/Users/n.t.wamsley/Desktop/20230324_OLEP08_200ng_30min_E20H50Y30_180K_2Th3p5ms_02.raw");
        //var rawFile = myThreadManager.CreateThreadAccessor();
        Console.WriteLine("Starting Conversion For: {0}", Path.GetFileNameWithoutExtension(inputFile));
        var rawFile = RawFileReaderAdapter.FileFactory(inputFile);
        if (!rawFile.IsOpen || rawFile.IsError)
        {
            // Check for any errors in the RAW file
            if (rawFile.IsError)
            {
                Console.WriteLine("Error opening ({0}) - {1}", rawFile.FileError.ErrorMessage, inputFile);
                rawFile.Dispose();
                return;
            }
            Console.WriteLine("Unable to access the RAW file using the RawFileReader class!");
            rawFile.Dispose();
            return;
        }
        //var rawFile = RawFileReaderAdapter.FileFactory(inputFile);

        // Get the number of instruments (controllers) present in the RAW file and set the 
        // selected instrument to the MS instrument, first instance of it
        //Console.WriteLine("The RAW file has data from {0} instruments" + rawFile.InstrumentCount);

        rawFile.SelectInstrument(Device.MS, 1);

        int firstScanNumber = rawFile.RunHeaderEx.FirstSpectrum;
        int lastScanNumber = rawFile.RunHeaderEx.LastSpectrum;
        // Build the ListArray
        var massField = new Field.Builder()
            .Name("mz_array")
            .DataType(new ListType(FloatType.Default))
            .Nullable(false)
            .Build();
        var intensityField = new Field.Builder()
            .Name("intensity_array")
            .DataType(new ListType(FloatType.Default))
            .Nullable(false)
            .Build();
        var scanHeaderField = new Field.Builder()
            .Name("scanHeader")
            .DataType(StringType.Default)
            .Nullable(false)
            .Build();
        var scanNumberField = new Field.Builder()
            .Name("scanNumber")
            .DataType(Int32Type.Default)
            .Nullable(false)
            .Build();
        var basePeakMzField = new Field.Builder()
            .Name("basePeakMz")
            .DataType(FloatType.Default)
            .Nullable(false)
            .Build();
        var basePeakIntensityField = new Field.Builder()
            .Name("basePeakIntensity")
            .DataType(FloatType.Default)
            .Nullable(false)
            .Build();
        var packetTypeField = new Field.Builder()
            .Name("packetType")
            .DataType(Int32Type.Default)
            .Nullable(false)
            .Build();
        var retentionTimeField = new Field.Builder()
            .Name("retentionTime")
            .DataType(FloatType.Default)
            .Nullable(false)
            .Build();
        var lowMzField = new Field.Builder()
            .Name("lowMz")
            .DataType(FloatType.Default)
            .Nullable(false)
            .Build();
        var highMzField = new Field.Builder()
            .Name("highMz")
            .DataType(FloatType.Default)
            .Nullable(false)
            .Build();
        var ticField = new Field.Builder()
            .Name("TIC")
            .DataType(FloatType.Default)
            .Nullable(false)
            .Build();
        var centerMzField = new Field.Builder()
            .Name("centerMz")
            .DataType(FloatType.Default)
            .Nullable(true)
            .Build();
        var isolationWidthMzField = new Field.Builder()
            .Name("isolationWidthMz")
            .DataType(FloatType.Default)
            .Nullable(true)
            .Build();
        var collisionEnergyField = new Field.Builder()
            .Name("collisionEnergyField")
            .DataType(FloatType.Default)
            .Nullable(true)
            .Build();
        var collisionEnergyEvField = new Field.Builder()
            .Name("collisionEnergyEvField")
            .DataType(FloatType.Default)
            .Nullable(true)
            .Build();
        var msOrderField = new Field.Builder()
            .Name("msOrder")
            .DataType(UInt8Type.Default)
            .Nullable(false)
            .Build();

        var schema = new Schema.Builder()
                            .Field(massField)
                            .Field(intensityField)
                            .Field(scanHeaderField)
                            .Field(scanNumberField)
                            .Field(basePeakMzField)
                            .Field(basePeakIntensityField)
                            .Field(packetTypeField)
                            .Field(retentionTimeField)
                            .Field(lowMzField)
                            .Field(highMzField)
                            .Field(ticField)
                            .Field(centerMzField)
                            .Field(isolationWidthMzField)
                            .Field(collisionEnergyField)
                            .Field(collisionEnergyEvField)
                            .Field(msOrderField)
                            .Build();
        // Get the start and end time from the RAW file
        var watch = new System.Diagnostics.Stopwatch();
        watch.Start();
                
        int hcdEnergyFieldIndex = -2; // -2 unknown, -1 not found in most recent scan, >=0 known index
        IRawFileThreadManager? scanThreadManager = null;
        List<ScanReaderWorker>? scanWorkers = null;
        ParallelOptions? scanParallelOptions = null;
        float[] massScratchBuffer = System.Array.Empty<float>();
        float[] intensityScratchBuffer = System.Array.Empty<float>();
        if (scanThreads > 1)
        {
            try
            {
                scanThreadManager = RawFileReaderFactory.CreateThreadManager(inputFile);
                scanWorkers = CreateScanWorkers(scanThreadManager, scanThreads);
                scanParallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = scanWorkers.Count
                };
            }
            catch (Exception ex)
            {
                if (scanWorkers != null)
                {
                    foreach (var worker in scanWorkers)
                    {
                        worker.Dispose();
                    }
                }

                scanThreadManager?.Dispose();
                scanWorkers = null;
                scanThreadManager = null;
                scanParallelOptions = null;
                Console.WriteLine(
                    "Warning: scan-thread mode unavailable for {0} ({1}: {2}). Falling back to single-thread scan extraction.",
                    Path.GetFileNameWithoutExtension(inputFile),
                    ex.GetType().Name,
                    ex.Message);
            }
        }

        using (var fileStream = new FileStream(
            outputFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1 << 20))
        using (var writer = new Apache.Arrow.Ipc.ArrowFileWriter(fileStream, schema))
        {
            writer.WriteStartAsync().GetAwaiter().GetResult();

            static void EnsureScratchCapacity(ref float[] buffer, int requiredLength)
            {
                if (buffer.Length >= requiredLength)
                {
                    return;
                }

                int newLength = buffer.Length == 0 ? requiredLength : Math.Max(requiredLength, buffer.Length * 2);
                float[] replacement = ArrayPool<float>.Shared.Rent(newLength);
                if (buffer.Length > 0)
                {
                    ArrayPool<float>.Shared.Return(buffer, clearArray: false);
                }

                buffer = replacement;
            }

            void AppendPeaks(
                double[] masses,
                double[] intensities,
                int centroidLength,
                FloatArray.Builder localMassValueBuilder,
                FloatArray.Builder localIntensityValueBuilder)
            {
                EnsureScratchCapacity(ref massScratchBuffer, centroidLength);
                EnsureScratchCapacity(ref intensityScratchBuffer, centroidLength);

                for (int j = 0; j < centroidLength; j++)
                {
                    massScratchBuffer[j] = (float)masses[j];
                    intensityScratchBuffer[j] = (float)intensities[j];
                }

                localMassValueBuilder.AppendRange(new ArraySegment<float>(massScratchBuffer, 0, centroidLength));
                localIntensityValueBuilder.AppendRange(new ArraySegment<float>(intensityScratchBuffer, 0, centroidLength));
            }

            RecordBatch BuildRecordBatch(int batchStart)
            {
                int batchEnd = Math.Min(batchStart + batchSize - 1, lastScanNumber);
                int batchRowCount = batchEnd - batchStart + 1;
                ulong batchPeakCount = 0;

                // Mass and intensity list builders
                var massListBuilder = new ListArray.Builder(FloatType.Default);
                var massValueBuilder = massListBuilder.ValueBuilder as FloatArray.Builder
                    ?? throw new InvalidOperationException("Expected float value builder for mz array");
                var intensityListBuilder = new ListArray.Builder(FloatType.Default);
                var intensityValueBuilder = intensityListBuilder.ValueBuilder as FloatArray.Builder
                    ?? throw new InvalidOperationException("Expected float value builder for intensity array");

                // Scan stats fields
                var scanHeaderBuilder = new StringArray.Builder();
                var scanNumberBuilder = new Int32Array.Builder();
                var basePeakMzBuilder = new FloatArray.Builder();
                var basePeakIntensityBuilder = new FloatArray.Builder();
                var packetTypeBuilder = new Int32Array.Builder();
                var retentionTimeBuilder = new FloatArray.Builder();
                var lowMzBuilder = new FloatArray.Builder();
                var highMzBuilder = new FloatArray.Builder();
                var ticBuilder = new FloatArray.Builder();

                // Scan event fields
                var centerMzBuilder = new FloatArray.Builder();
                var isolationWidthMzBuilder = new FloatArray.Builder();
                var collisionEnergyBuilder = new FloatArray.Builder();
                var collisionEnergyEvBuilder = new FloatArray.Builder();
                var msOrderBuilder = new UInt8Array.Builder();

                var basePeakMzCache = new float[batchRowCount];
                var packetTypeCache = new int[batchRowCount];
                var basePeakIntensityCache = new float[batchRowCount];
                var retentionTimeCache = new float[batchRowCount];
                var lowMzCache = new float[batchRowCount];
                var highMzCache = new float[batchRowCount];
                var ticCache = new float[batchRowCount];

                for (int rowIndex = 0; rowIndex < batchRowCount; rowIndex++)
                {
                    int scanNumber = batchStart + rowIndex;
                    var scanStats = rawFile.GetScanStatsForScanNumber(scanNumber);
                    batchPeakCount += (ulong)scanStats.PacketCount;
                    basePeakMzCache[rowIndex] = (float)scanStats.BasePeakMass;
                    packetTypeCache[rowIndex] = scanStats.PacketType;
                    basePeakIntensityCache[rowIndex] = (float)scanStats.BasePeakIntensity;
                    retentionTimeCache[rowIndex] = (float)scanStats.StartTime;
                    lowMzCache[rowIndex] = (float)scanStats.LowMass;
                    highMzCache[rowIndex] = (float)scanStats.HighMass;
                    ticCache[rowIndex] = (float)scanStats.TIC;
                }

                massListBuilder.Reserve(batchRowCount);
                intensityListBuilder.Reserve(batchRowCount);
                scanNumberBuilder.Reserve(batchRowCount);
                basePeakMzBuilder.Reserve(batchRowCount);
                basePeakIntensityBuilder.Reserve(batchRowCount);
                packetTypeBuilder.Reserve(batchRowCount);
                retentionTimeBuilder.Reserve(batchRowCount);
                lowMzBuilder.Reserve(batchRowCount);
                highMzBuilder.Reserve(batchRowCount);
                ticBuilder.Reserve(batchRowCount);
                centerMzBuilder.Reserve(batchRowCount);
                isolationWidthMzBuilder.Reserve(batchRowCount);
                collisionEnergyBuilder.Reserve(batchRowCount);
                collisionEnergyEvBuilder.Reserve(batchRowCount);
                msOrderBuilder.Reserve(batchRowCount);
                massValueBuilder.Reserve((int)batchPeakCount);
                intensityValueBuilder.Reserve((int)batchPeakCount);

                void AppendBufferedRow(
                    int localIndex,
                    int scanNumber,
                    int rowIndex,
                    double[][] chunkMasses,
                    double[][] chunkIntensities,
                    int[] chunkCentroidLengths,
                    string[] chunkScanHeaders,
                    byte[] chunkMsOrders,
                    float[] chunkCenterMz,
                    float[] chunkIsolationWidthMz,
                    float[] chunkCollisionEnergy,
                    float[] chunkCollisionEnergyEv,
                    bool[] chunkHasCollisionEnergyEv)
                {
                    var masses = chunkMasses[localIndex];
                    var intensities = chunkIntensities[localIndex];
                    int centroidLength = chunkCentroidLengths[localIndex];

                    massListBuilder.Append();
                    intensityListBuilder.Append();
                    AppendPeaks(masses, intensities, centroidLength, massValueBuilder, intensityValueBuilder);

                    scanHeaderBuilder.Append(chunkScanHeaders[localIndex]);
                    scanNumberBuilder.Append(scanNumber);
                    basePeakMzBuilder.Append(basePeakMzCache[rowIndex]);
                    packetTypeBuilder.Append(packetTypeCache[rowIndex]);
                    basePeakIntensityBuilder.Append(basePeakIntensityCache[rowIndex]);
                    retentionTimeBuilder.Append(retentionTimeCache[rowIndex]);
                    lowMzBuilder.Append(lowMzCache[rowIndex]);
                    highMzBuilder.Append(highMzCache[rowIndex]);
                    ticBuilder.Append(ticCache[rowIndex]);

                    byte msOrder = chunkMsOrders[localIndex];
                    if (msOrder > 1)
                    {
                        centerMzBuilder.Append(chunkCenterMz[localIndex]);
                        isolationWidthMzBuilder.Append(chunkIsolationWidthMz[localIndex]);
                        collisionEnergyBuilder.Append(chunkCollisionEnergy[localIndex]);
                        if (chunkHasCollisionEnergyEv[localIndex])
                        {
                            collisionEnergyEvBuilder.Append(chunkCollisionEnergyEv[localIndex]);
                        }
                        else
                        {
                            collisionEnergyEvBuilder.AppendNull();
                        }
                    }
                    else
                    {
                        centerMzBuilder.AppendNull();
                        isolationWidthMzBuilder.AppendNull();
                        collisionEnergyBuilder.AppendNull();
                        collisionEnergyEvBuilder.AppendNull();
                    }

                    msOrderBuilder.Append(msOrder);
                }

                // Read batch from raw file
                if (scanWorkers != null && scanParallelOptions != null)
                {
                    int workerCount = scanWorkers.Count;
                    int chunkBufferSize = Math.Min(scanChunkSize, batchRowCount);
                    var chunkMasses = new double[chunkBufferSize][];
                    var chunkIntensities = new double[chunkBufferSize][];
                    var chunkCentroidLengths = new int[chunkBufferSize];
                    var chunkScanHeaders = new string[chunkBufferSize];
                    var chunkMsOrders = new byte[chunkBufferSize];
                    var chunkCenterMz = new float[chunkBufferSize];
                    var chunkIsolationWidthMz = new float[chunkBufferSize];
                    var chunkCollisionEnergy = new float[chunkBufferSize];
                    var chunkCollisionEnergyEv = new float[chunkBufferSize];
                    var chunkHasCollisionEnergyEv = new bool[chunkBufferSize];

                    for (int chunkStart = batchStart; chunkStart <= batchEnd; chunkStart += scanChunkSize)
                    {
                        int chunkEnd = Math.Min(chunkStart + scanChunkSize - 1, batchEnd);
                        int chunkCount = chunkEnd - chunkStart + 1;

                        Parallel.For(
                            0,
                            workerCount,
                            scanParallelOptions,
                            workerIndex =>
                            {
                                var worker = scanWorkers[workerIndex];
                                for (int localIndex = workerIndex; localIndex < chunkCount; localIndex += workerCount)
                                {
                                    int scanNumber = chunkStart + localIndex;
                                    ReadScanRowIntoBuffers(
                                        worker.RawFile,
                                        scanNumber,
                                        ref worker.HcdEnergyFieldIndex,
                                        localIndex,
                                        chunkMasses,
                                        chunkIntensities,
                                        chunkCentroidLengths,
                                        chunkScanHeaders,
                                        chunkMsOrders,
                                        chunkCenterMz,
                                        chunkIsolationWidthMz,
                                        chunkCollisionEnergy,
                                        chunkCollisionEnergyEv,
                                        chunkHasCollisionEnergyEv);
                                }
                            });

                        for (int localIndex = 0; localIndex < chunkCount; localIndex++)
                        {
                            int scanNumber = chunkStart + localIndex;
                            int rowIndex = scanNumber - batchStart;
                            AppendBufferedRow(
                                localIndex,
                                scanNumber,
                                rowIndex,
                                chunkMasses,
                                chunkIntensities,
                                chunkCentroidLengths,
                                chunkScanHeaders,
                                chunkMsOrders,
                                chunkCenterMz,
                                chunkIsolationWidthMz,
                                chunkCollisionEnergy,
                                chunkCollisionEnergyEv,
                                chunkHasCollisionEnergyEv);
                        }
                    }
                }
                else
                {
                    for (int scanNumber = batchStart; scanNumber <= batchEnd; scanNumber++)
                    {
                        int rowIndex = scanNumber - batchStart;
                        var scan = Scan.FromFile(rawFile, scanNumber);
                        var centroidScan = scan.CentroidScan;
                        var masses = centroidScan.Masses;
                        var intensities = centroidScan.Intensities;
                        int centroidLength = centroidScan.Length;

                        massListBuilder.Append();
                        intensityListBuilder.Append();
                        AppendPeaks(masses, intensities, centroidLength, massValueBuilder, intensityValueBuilder);

                        scanHeaderBuilder.Append(rawFile.GetFilterForScanNumber(scanNumber).ToString());
                        scanNumberBuilder.Append(scanNumber);
                        basePeakMzBuilder.Append(basePeakMzCache[rowIndex]);
                        packetTypeBuilder.Append(packetTypeCache[rowIndex]);
                        basePeakIntensityBuilder.Append(basePeakIntensityCache[rowIndex]);
                        retentionTimeBuilder.Append(retentionTimeCache[rowIndex]);
                        lowMzBuilder.Append(lowMzCache[rowIndex]);
                        highMzBuilder.Append(highMzCache[rowIndex]);
                        ticBuilder.Append(ticCache[rowIndex]);

                        var scanEvent = rawFile.GetScanEventForScanNumber(scanNumber);
                        if ((byte)scanEvent.MSOrder > 1)
                        {
                            centerMzBuilder.Append((float)scanEvent.GetMass(0));
                            isolationWidthMzBuilder.Append((float)scanEvent.GetIsolationWidth(0) + (float)scanEvent.GetIsolationWidthOffset(0));
                            collisionEnergyBuilder.Append((float)scanEvent.GetEnergy(0));

                            float ev = 0.0f;
                            bool foundEv = false;
                            var trailerData = rawFile.GetTrailerExtraInformation(scanNumber);
                            if (TryResolveHcdEnergyFieldIndex(trailerData.Labels, trailerData.Length, ref hcdEnergyFieldIndex))
                            {
                                string energyValue = trailerData.Values[hcdEnergyFieldIndex].Trim();
                                foundEv = TryParseCollisionEnergyEv(energyValue, out ev);
                            }

                            if (!foundEv)
                            {
                                collisionEnergyEvBuilder.AppendNull();
                            }
                            else
                            {
                                collisionEnergyEvBuilder.Append(ev);
                            }
                        }
                        else
                        {
                            centerMzBuilder.AppendNull();
                            isolationWidthMzBuilder.AppendNull();
                            collisionEnergyBuilder.AppendNull();
                            collisionEnergyEvBuilder.AppendNull();
                        }

                        msOrderBuilder.Append((byte)scanEvent.MSOrder);
                    }
                }

                var massArray = massListBuilder.Build();
                var intensityArray = intensityListBuilder.Build();
                IArrowArray scanHeaderArray = scanHeaderBuilder.Build();
                IArrowArray scanNumberArray = scanNumberBuilder.Build();
                IArrowArray basePeakMzArray = basePeakMzBuilder.Build();
                IArrowArray basePeakIntensityArray = basePeakIntensityBuilder.Build();
                IArrowArray packetTypeArray = packetTypeBuilder.Build();
                IArrowArray retentionTimeArray = retentionTimeBuilder.Build();
                IArrowArray lowMzArray = lowMzBuilder.Build();
                IArrowArray highMzArray = highMzBuilder.Build();
                IArrowArray ticArray = ticBuilder.Build();
                IArrowArray centerMzArray = centerMzBuilder.Build();
                IArrowArray isolationWidthMzArray = isolationWidthMzBuilder.Build();
                IArrowArray collisionEnergyArray = collisionEnergyBuilder.Build();
                IArrowArray collisionEnergyEvArray = collisionEnergyEvBuilder.Build();
                IArrowArray msOrderArray = msOrderBuilder.Build();
                return new RecordBatch(schema, new[] {
                    massArray,
                    intensityArray,
                    scanHeaderArray,
                    scanNumberArray,
                    basePeakMzArray,
                    basePeakIntensityArray,
                    packetTypeArray,
                    retentionTimeArray,
                    lowMzArray,
                    highMzArray,
                    ticArray,
                    centerMzArray,
                    isolationWidthMzArray,
                    collisionEnergyArray,
                    collisionEnergyEvArray,
                    msOrderArray }, batchRowCount);
            }

            for (int batchStart = firstScanNumber; batchStart <= lastScanNumber; batchStart += batchSize)
            {
                writer.WriteRecordBatch(BuildRecordBatch(batchStart));
            }

            writer.WriteEndAsync().GetAwaiter().GetResult(); // Finish the Arrow file
        }
        watch.Stop();
        Console.WriteLine("Execution Time: {0} ms for {1}", watch.ElapsedMilliseconds, Path.GetFileNameWithoutExtension(inputFile));
        if (scanWorkers != null)
        {
            foreach (var worker in scanWorkers)
            {
                worker.Dispose();
            }
        }
        scanThreadManager?.Dispose();
        rawFile.Dispose();
        if (massScratchBuffer.Length > 0)
        {
            ArrayPool<float>.Shared.Return(massScratchBuffer, clearArray: false);
        }
        if (intensityScratchBuffer.Length > 0)
        {
            ArrayPool<float>.Shared.Return(intensityScratchBuffer, clearArray: false);
        }
    }

    sealed class ScanReaderWorker : IDisposable
    {
        public IRawDataPlus RawFile { get; }
        public int HcdEnergyFieldIndex = -2;

        private ScanReaderWorker(IRawDataPlus rawFile)
        {
            RawFile = rawFile;
            RawFile.SelectInstrument(Device.MS, 1);
        }

        public static ScanReaderWorker Create(IRawFileThreadManager threadManager)
        {
            return new ScanReaderWorker((IRawDataPlus)threadManager.CreateThreadAccessor());
        }

        public void Dispose()
        {
            RawFile.Dispose();
        }
    }

    static List<ScanReaderWorker> CreateScanWorkers(IRawFileThreadManager threadManager, int scanThreads)
    {
        int workerCount = Math.Max(1, scanThreads);
        var workers = new List<ScanReaderWorker>(workerCount);
        try
        {
            for (int i = 0; i < workerCount; i++)
            {
                workers.Add(ScanReaderWorker.Create(threadManager));
            }
        }
        catch
        {
            foreach (var worker in workers)
            {
                worker.Dispose();
            }

            throw;
        }

        return workers;
    }

    static void ReadScanRowIntoBuffers(
        IRawDataPlus rawFile,
        int scanNumber,
        ref int hcdEnergyFieldIndex,
        int bufferIndex,
        double[][] massesBuffer,
        double[][] intensitiesBuffer,
        int[] centroidLengthBuffer,
        string[] scanHeaderBuffer,
        byte[] msOrderBuffer,
        float[] centerMzBuffer,
        float[] isolationWidthMzBuffer,
        float[] collisionEnergyBuffer,
        float[] collisionEnergyEvBuffer,
        bool[] hasCollisionEnergyEvBuffer)
    {
        var scan = Scan.FromFile(rawFile, scanNumber);
        var centroidScan = scan.CentroidScan;
        massesBuffer[bufferIndex] = centroidScan.Masses;
        intensitiesBuffer[bufferIndex] = centroidScan.Intensities;
        centroidLengthBuffer[bufferIndex] = centroidScan.Length;
        scanHeaderBuffer[bufferIndex] = rawFile.GetFilterForScanNumber(scanNumber).ToString();

        var scanEvent = rawFile.GetScanEventForScanNumber(scanNumber);
        byte msOrder = (byte)scanEvent.MSOrder;
        msOrderBuffer[bufferIndex] = msOrder;
        hasCollisionEnergyEvBuffer[bufferIndex] = false;

        if (msOrder <= 1)
        {
            return;
        }

        centerMzBuffer[bufferIndex] = (float)scanEvent.GetMass(0);
        isolationWidthMzBuffer[bufferIndex] = (float)scanEvent.GetIsolationWidth(0) + (float)scanEvent.GetIsolationWidthOffset(0);
        collisionEnergyBuffer[bufferIndex] = (float)scanEvent.GetEnergy(0);

        float ev = 0.0f;
        bool foundEv = false;
        var trailerData = rawFile.GetTrailerExtraInformation(scanNumber);
        if (TryResolveHcdEnergyFieldIndex(trailerData.Labels, trailerData.Length, ref hcdEnergyFieldIndex))
        {
            string energyValue = trailerData.Values[hcdEnergyFieldIndex].Trim();
            foundEv = TryParseCollisionEnergyEv(energyValue, out ev);
        }

        if (foundEv)
        {
            collisionEnergyEvBuffer[bufferIndex] = ev;
            hasCollisionEnergyEvBuffer[bufferIndex] = true;
        }
    }

    static bool TryResolveHcdEnergyFieldIndex(IReadOnlyList<string> labels, int trailerLength, ref int hcdEnergyFieldIndex)
    {
        int labelCount = Math.Min(trailerLength, labels.Count);
        if (hcdEnergyFieldIndex >= 0 &&
            hcdEnergyFieldIndex < labelCount &&
            labels[hcdEnergyFieldIndex] == HcdEnergyTrailerLabel)
        {
            return true;
        }

        for (int j = 0; j < labelCount; j++)
        {
            if (labels[j] == HcdEnergyTrailerLabel)
            {
                hcdEnergyFieldIndex = j;
                return true;
            }
        }

        hcdEnergyFieldIndex = -1;
        return false;
    }

    static bool TryParseCollisionEnergyEv(string energyValue, out float ev)
    {
        ev = 0.0f;
        if (energyValue.Contains(','))
        {
            float sum = 0.0f;
            int count = 0;
            string[] energyValues = energyValue.Split(',');
            foreach (string value in energyValues)
            {
                if (float.TryParse(value.Trim(), out float parsedValue))
                {
                    sum += parsedValue;
                    count++;
                }
            }

            if (count == 0)
            {
                return false;
            }

            ev = sum / count;
            return true;
        }

        return float.TryParse(energyValue, out ev);
    }

    static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s";
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return $"{elapsed.Minutes}m {elapsed.Seconds}s";
        }

        if (elapsed.TotalSeconds >= 1)
        {
            return $"{elapsed.TotalSeconds:F1}s";
        }

        return $"{elapsed.TotalMilliseconds:F0}ms";
    }

}
