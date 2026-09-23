// All using statements must come first
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;

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
    public bool HasArgumentError { get; set; } = false;
    public bool ShouldShowVersion { get; set; } = false;
    public int BatchSize { get; set; } = 1000;
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
                        options.HasArgumentError = true;
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
                        options.HasArgumentError = true;
                        options.ShouldShowHelp = true;
                        return options;
                    }
                    break;
                case "--skip-existing":
                    options.SkipExisting = true;
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
                        options.HasArgumentError = true;
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
                        options.HasArgumentError = true;
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
                        options.HasArgumentError = true;
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
                        options.HasArgumentError = true;
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
        Console.WriteLine("  -b, --batch-size <size>    Maximum scans in each Arrow batch (default: 1000)");
        Console.WriteLine("  -o, --output-dir <path>    Output directory for .arrow files (default: <input_dir>/arrow_out)");
        Console.WriteLine("      --skip-existing        Skip conversion when existing output appears complete");
        Console.WriteLine("  -t, --threads-per-file <n> Scan extraction threads used for each file (default: 3)");
        Console.WriteLine("      --scan-chunk-size <n>  Maximum decoded scans per chunk (default: 128)");
        Console.WriteLine("      --version              Show version information");
        Console.WriteLine("  -h, --help                 Show help information");
    }
}

internal static class Program
{
    private const string HcdEnergyTrailerLabel = "HCD Energy V:";
    private const string IonInjectionTimeTrailerLabelFragment = "ion injection time";
    private const string ScanNumberColumnName = "scanNumber";
    private const string FillTimeMsColumnName = "fillTimeMs";
    private const string CycleIndexColumnName = "cycle_idx";
    private static readonly string[] RequiredOutputColumnNames = { ScanNumberColumnName, FillTimeMsColumnName, CycleIndexColumnName };

    public static int Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            return Run(args, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Conversion cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Conversion failed: {UnwrapThreadManagerException(ex).Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static int Run(string[] args, CancellationToken cancellationToken)
    {
        var options = Options.ParseArguments(args);

        if (options.HasArgumentError)
        {
            Options.ShowHelp();
            return 2;
        }

        if (options.ShouldShowVersion)
        {
            Console.WriteLine($"{AppMetadata.AppName} {AppMetadata.Version}");
            return 0;
        }

        if (options.ShouldShowHelp)
        {
            Options.ShowHelp();
            return 0;
        }
	
        if (string.IsNullOrEmpty(options.RawPath))
        {
            Console.WriteLine("Missing required RAW_PATH argument.");
            Options.ShowHelp();
            return 2;
        }

        options.BatchSize = Math.Max(1, options.BatchSize);
        options.ThreadsPerFile = Math.Max(1, options.ThreadsPerFile);
        options.ScanChunkSize = Math.Max(1, options.ScanChunkSize);
	
        var totalExecutionWatch = Stopwatch.StartNew();

        bool rawPathIsFile = File.Exists(options.RawPath);
        bool rawPathIsDirectory = Directory.Exists(options.RawPath);
        if (!rawPathIsFile && !rawPathIsDirectory)
        {
            Console.WriteLine($"{AppMetadata.AppName} {AppMetadata.Version}");
            Console.WriteLine("File or Directory does not exist: {0}", options.RawPath);
            return 1;
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
                return 0;
            }

            input_dir = inputFileDirectory;
        }

        string[] file_paths = GetFilePaths(options.RawPath);
        string output_dir = buildOutputDir(input_dir, options.OutputDir);
        if (string.IsNullOrEmpty(output_dir))
        {
            return 1;
        }

        string[] output_paths = getOutputPaths(output_dir, file_paths);
        int skippedCompleteFiles = 0;
        int convertedFiles = 0;

        Console.WriteLine($"{AppMetadata.AppName} {AppMetadata.Version}");
        Console.WriteLine("==================================================");
        Console.WriteLine($"Config: threads-per-file={options.ThreadsPerFile}  scan-chunk-size={options.ScanChunkSize}  batch-size={options.BatchSize}");
        Console.WriteLine($"Config: output={output_dir}");
        Console.WriteLine($"Config: skip-existing={options.SkipExisting.ToString().ToLowerInvariant()}");
        Console.WriteLine();
        Console.WriteLine($"Input : {inputMode} {options.RawPath}");
        Console.WriteLine($"Queue : discovered={file_paths.Length}");
        Console.WriteLine("==================================================");

        if (file_paths.Length == 0)
        {
            totalExecutionWatch.Stop();
            Console.WriteLine("No .raw files found to process");
            Console.WriteLine("Total conversion time: {0}", FormatDuration(totalExecutionWatch.Elapsed));
            return 0;
        }

        foreach (int fileIndex in Enumerable.Range(0, file_paths.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.SkipExisting && File.Exists(output_paths[fileIndex]) &&
                HasCompleteExistingOutput(file_paths[fileIndex], output_paths[fileIndex]))
            {
                skippedCompleteFiles++;
                continue;
            }

            ProcessFile(file_paths[fileIndex], output_paths[fileIndex], options.BatchSize,
                options.ThreadsPerFile, options.ScanChunkSize, cancellationToken);
            convertedFiles++;
        }
        Console.WriteLine($"Completed: converted={convertedFiles} skipped-complete={skippedCompleteFiles}");

        totalExecutionWatch.Stop();
        Console.WriteLine("Total conversion time: {0}", FormatDuration(totalExecutionWatch.Elapsed));
        return 0;
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
            System.Array.Sort(file_paths, StringComparer.Ordinal);
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
                    ValidateRequiredOutputColumns(batch.Schema, outputFile);

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

    private static void ValidateRequiredOutputColumns(Schema schema, string outputFile)
    {
        foreach (string requiredColumnName in RequiredOutputColumnNames)
        {
            bool foundColumn = false;
            for (int i = 0; i < schema.FieldsList.Count; i++)
            {
                if (string.Equals(schema.FieldsList[i].Name, requiredColumnName, StringComparison.Ordinal))
                {
                    foundColumn = true;
                    break;
                }
            }

            if (!foundColumn)
            {
                throw new InvalidDataException($"Missing required column '{requiredColumnName}' in output file: {outputFile}");
            }
        }
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

    private static Exception UnwrapThreadManagerException(Exception exception)
    {
        Exception current = exception;
        while (true)
        {
            Exception? next = current switch
            {
                TargetInvocationException { InnerException: not null } targetInvocationException => targetInvocationException.InnerException,
                TypeInitializationException { InnerException: not null } typeInitializationException => typeInitializationException.InnerException,
                AggregateException { InnerExceptions.Count: 1 } aggregateException => aggregateException.InnerExceptions[0],
                _ => null
            };

            if (next == null)
            {
                return current;
            }

            current = next;
        }
    }

    private static string DescribeExceptionChain(Exception exception)
    {
        List<string> parts = new List<string>();
        Exception? current = exception;
        while (current != null)
        {
            parts.Add($"{current.GetType().FullName}: {current.Message}");
            current = current.InnerException;
        }

        return string.Join(" --> ", parts);
    }

    private static string FormatThreadManagerException(Exception exception)
    {
        Exception rootCause = UnwrapThreadManagerException(exception);
        StringBuilder builder = new StringBuilder();
        builder.Append("Root cause: ");
        builder.Append(rootCause.GetType().FullName);
        builder.Append(": ");
        builder.Append(rootCause.Message);

        string exceptionChain = DescribeExceptionChain(exception);
        string rootCauseSummary = $"{rootCause.GetType().FullName}: {rootCause.Message}";
        if (!string.Equals(exceptionChain, rootCauseSummary, StringComparison.Ordinal))
        {
            builder.AppendLine();
            builder.Append("Exception chain: ");
            builder.Append(exceptionChain);
        }

        string? stackTrace = !string.IsNullOrWhiteSpace(rootCause.StackTrace)
            ? rootCause.StackTrace
            : exception.StackTrace;
        if (!string.IsNullOrWhiteSpace(stackTrace))
        {
            builder.AppendLine();
            builder.Append("Stack trace:");
            builder.AppendLine();
            builder.Append(stackTrace.Trim());
        }

        return builder.ToString();
    }

    // These budgets limit retained payloads, not SDK allocations or total process RSS.
    private const long ChunkTargetBytes = 8L << 20;
    private const long BatchTargetBytes = 32L << 20;
    private const long DecodedQueueBytes = 32L << 20;
    private const long BatchQueueBytes = 64L << 20;

    static void ProcessFile(string inputFile, string outputFile, int batchSize, int scanThreads,
        int scanChunkSize, CancellationToken cancellationToken)
    {
        Console.WriteLine("Starting Conversion For: {0}", Path.GetFileNameWithoutExtension(inputFile));
        var watch = Stopwatch.StartNew();
        using var viewManager = new CachedViewManager(inputFile);
        using var rawFile = RawFileReaderAdapter.DelegatedAccessFileFactory(inputFile, viewManager);
        if (!rawFile.IsOpen || rawFile.IsError)
            throw new IOException($"Unable to read RAW file: {inputFile}; SDK error: {rawFile.FileError.ErrorMessage}");
        if (!viewManager.WasUsed)
            throw new InvalidOperationException("The Thermo SDK did not use the delegated RAW reader.");
        rawFile.SelectInstrument(Device.MS, 1);
        int firstScanNumber = rawFile.RunHeaderEx.FirstSpectrum;
        int lastScanNumber = rawFile.RunHeaderEx.LastSpectrum;
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
        var fillTimeMsField = new Field.Builder()
            .Name(FillTimeMsColumnName)
            .DataType(FloatType.Default)
            .Nullable(true)
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
        var cycleIdxField = new Field.Builder()
            .Name(CycleIndexColumnName)
            .DataType(Int32Type.Default)
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
                            .Field(fillTimeMsField)
                            .Field(lowMzField)
                            .Field(highMzField)
                            .Field(ticField)
                            .Field(centerMzField)
                            .Field(isolationWidthMzField)
                            .Field(collisionEnergyField)
                            .Field(collisionEnergyEvField)
                            .Field(msOrderField)
                            .Field(cycleIdxField)
                            .Build();

        IRawFileThreadManager? scanThreadManager = null;
        List<ScanReaderWorker>? scanWorkers = null;
        string temporaryOutput = outputFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (scanThreads > 1)
            {
                try
                {
                    scanThreadManager = RawFileReaderAdapter.DelegatedRandomAccessThreadedFileFactory(inputFile, viewManager);
                    scanWorkers = CreateScanWorkers(scanThreadManager, scanThreads);
                }
                catch (Exception ex)
                {
                    scanThreadManager?.Dispose();
                    scanThreadManager = null;
                    Console.WriteLine("Warning: scan-thread mode unavailable for {0}. Falling back to single-thread scan extraction.", Path.GetFileName(inputFile));
                    Console.WriteLine("Warning details:{0}{1}", Environment.NewLine, FormatThreadManagerException(ex));
                }
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = cancellation.Token;
            using var chunks = new BoundedPipelineQueue<ScanChunk>(2, DecodedQueueBytes, token);
            using var batches = new BoundedPipelineQueue<RecordBatch>(2, BatchQueueBytes, token, batch => batch.Dispose());
            ExceptionDispatchInfo? failure = null;
            void Fail(Exception ex)
            {
                // A cancellation caused by another stage must not hide its original failure.
                Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(ex), null);
                cancellation.Cancel();
            }
            long extractionTicks = 0, assemblyTicks = 0, writingTicks = 0;
            int chunkCount = 0, batchCount = 0;
            var producer = Task.Run(() =>
            {
                try
                {
                    int hcdIndex = -2, fillIndex = -2;
                    // Reuse fixed reader workers for the current file. Repeated Parallel.For
                    // calls varied reader participation while the ThreadPool adapted to I/O waits.
                    // The team is joined before this producer finishes and readers are disposed.
                    using var workerTeam = scanWorkers == null ? null : new ScanWorkerTeam(scanWorkers.Count);
                    for (long start = firstScanNumber; start <= lastScanNumber;)
                    {
                        token.ThrowIfCancellationRequested();
                        long started = Stopwatch.GetTimestamp();
                        // Cap the reference array as well as payload bytes, even for extreme CLI values.
                        int capacity = (int)Math.Min(Math.Min(scanChunkSize, 65536), lastScanNumber - start + 1);
                        var rows = new ScanRow[capacity];
                        int next = -1;
                        long bytes = 0;
                        void Extract(IRawDataPlus reader, ref int hcd, ref int fill)
                        {
                            while (Volatile.Read(ref bytes) < ChunkTargetBytes)
                            {
                                token.ThrowIfCancellationRequested();
                                int index = Interlocked.Increment(ref next);
                                if (index >= capacity) break;
                                var row = ReadScanRow(reader, checked((int)start + index), ref hcd, ref fill);
                                rows[index] = row;
                                Interlocked.Add(ref bytes, row.DecodedBytes);
                            }
                        }
                        if (scanWorkers == null)
                            Extract(rawFile, ref hcdIndex, ref fillIndex);
                        else
                            workerTeam!.Run(i =>
                            {
                                var worker = scanWorkers[i];
                                Extract(worker.RawFile, ref worker.HcdEnergyFieldIndex, ref worker.FillTimeFieldIndex);
                            });
                        int count = Math.Min(next + 1, capacity);
                        extractionTicks += Stopwatch.GetTimestamp() - started;
                        chunks.Add(new ScanChunk(rows, count), bytes + 8L * capacity);
                        chunkCount++;
                        start += count;
                    }
                }
                catch (Exception ex) { Fail(ex); }
                finally { chunks.Complete(); }
            });
            var assembler = Task.Run(() =>
            {
                try
                {
                    var cycle = new CycleIndexTracker();
                    using var scratch = new PeakScratch();
                    ArrowBatchBuilder? builder = null;
                    void Publish()
                    {
                        if (builder == null) return;
                        long started = Stopwatch.GetTimestamp();
                        var batch = builder.Build(schema);
                        long bytes = builder.EstimatedBytes;
                        builder = null;
                        assemblyTicks += Stopwatch.GetTimestamp() - started;
                        try { batches.Add(batch, bytes); }
                        catch { batch.Dispose(); throw; }
                    }
                    while (true)
                    {
                        using var lease = chunks.Read();
                        if (lease == null) break;
                        var chunk = lease.Value;
                        for (int i = 0; i < chunk.Count; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            var row = chunk.Rows[i];
                            // Leave room before appending a large row; one indivisible row may exceed the target.
                            if (builder != null && builder.EstimatedBytes + row.ArrowBytes > BatchTargetBytes)
                                Publish();
                            long started = Stopwatch.GetTimestamp();
                            builder ??= new ArrowBatchBuilder(Math.Min(batchSize, 65536));
                            builder.Append(row, cycle, scratch);
                            chunk.Rows[i] = null!; // SDK arrays become collectible as soon as copied.
                            assemblyTicks += Stopwatch.GetTimestamp() - started;
                            if (builder.Count >= batchSize || builder.EstimatedBytes >= BatchTargetBytes)
                                Publish();
                        }
                    }
                    Publish();
                }
                catch (Exception ex) { Fail(ex); }
                finally { batches.Complete(); }
            });
            try
            {
                using var fileStream = new FileStream(temporaryOutput, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 1 << 20);
                using var writer = new ArrowFileWriter(fileStream, schema);
                long started = Stopwatch.GetTimestamp();
                writer.WriteStart();
                writingTicks += Stopwatch.GetTimestamp() - started;
                while (true)
                {
                    using var lease = batches.Read();
                    if (lease == null) break;
                    started = Stopwatch.GetTimestamp();
                    writer.WriteRecordBatch(lease.Value);
                    writingTicks += Stopwatch.GetTimestamp() - started;
                    batchCount++;
                }
                failure?.Throw();
                token.ThrowIfCancellationRequested();
                started = Stopwatch.GetTimestamp();
                writer.WriteEnd();
                fileStream.Flush();
                writingTicks += Stopwatch.GetTimestamp() - started;
            }
            catch (Exception ex) { Fail(ex); }
            finally
            {
                // Joining before disposal is essential: no SDK accessor or Arrow buffer may outlive this file.
                cancellation.Cancel();
                Task.WaitAll(producer, assembler);
            }
            failure?.Throw();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryOutput, outputFile, overwrite: true);
            watch.Stop();
            static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine(FormattableString.Invariant($"PERF extraction_ms={Milliseconds(extractionTicks):F3} assembly_ms={Milliseconds(assemblyTicks):F3} writing_ms={Milliseconds(writingTicks):F3} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} decoded_queue_peak_bytes={chunks.PeakBytes} batch_queue_peak_bytes={batches.PeakBytes} chunks={chunkCount} batches={batchCount}"));
            Console.WriteLine("Execution Time: {0} ms for {1}", watch.ElapsedMilliseconds, Path.GetFileNameWithoutExtension(inputFile));
        }
        finally
        {
            try { DisposeScanReaders(scanWorkers, scanThreadManager); }
            finally { if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput); }
        }
    }

    sealed record ScanChunk(ScanRow[] Rows, int Count);

    sealed class ScanRow
    {
        public double[] Masses = null!, Intensities = null!;
        public int Length, ScanNumber, PacketType;
        public string Header = "";
        public float BasePeakMz, BasePeakIntensity, RetentionTime, LowMz, HighMz, Tic;
        public float CenterMz, IsolationWidthMz, CollisionEnergy, CollisionEnergyEv, FillTimeMs;
        public byte MsOrder;
        public bool HasCollisionEnergyEv, HasFillTimeMs;
        public long DecodedBytes => 256L + 8L * ((Masses?.LongLength ?? 0) + (Intensities?.LongLength ?? 0)) + 2L * Header.Length;
        public long ArrowBytes => 128L + 8L * Length + Encoding.UTF8.GetByteCount(Header);
    }

    sealed class PeakScratch : IDisposable
    {
        private float[] masses = System.Array.Empty<float>(), intensities = System.Array.Empty<float>();
        private static void Grow(ref float[] buffer, int length)
        {
            if (buffer.Length >= length) return;
            var replacement = ArrayPool<float>.Shared.Rent(length);
            if (buffer.Length > 0) ArrayPool<float>.Shared.Return(buffer);
            buffer = replacement;
        }
        public void Append(ScanRow row, FloatArray.Builder massBuilder, FloatArray.Builder intensityBuilder)
        {
            Grow(ref masses, row.Length);
            Grow(ref intensities, row.Length);
            for (int i = 0; i < row.Length; i++)
            {
                masses[i] = (float)row.Masses[i];
                intensities[i] = (float)row.Intensities[i];
            }
            massBuilder.AppendRange(new ArraySegment<float>(masses, 0, row.Length));
            intensityBuilder.AppendRange(new ArraySegment<float>(intensities, 0, row.Length));
        }
        public void Dispose()
        {
            if (masses.Length > 0) ArrayPool<float>.Shared.Return(masses);
            if (intensities.Length > 0) ArrayPool<float>.Shared.Return(intensities);
        }
    }

    sealed class ArrowBatchBuilder
    {
        private readonly ListArray.Builder masses = new(FloatType.Default), intensities = new(FloatType.Default);
        private readonly StringArray.Builder headers = new();
        private readonly Int32Array.Builder scans = new(), packets = new(), cycles = new();
        private readonly FloatArray.Builder baseMz = new(), baseIntensity = new(), retention = new(), fill = new(),
            low = new(), high = new(), tic = new(), center = new(), width = new(), energy = new(), ev = new();
        private readonly UInt8Array.Builder order = new();
        public int Count { get; private set; }
        public long EstimatedBytes { get; private set; }
        public ArrowBatchBuilder(int capacity)
        {
            masses.Reserve(capacity); intensities.Reserve(capacity);
            scans.Reserve(capacity); packets.Reserve(capacity); cycles.Reserve(capacity); order.Reserve(capacity);
            foreach (var field in new[] { baseMz, baseIntensity, retention, fill, low, high, tic, center, width, energy, ev })
                field.Reserve(capacity);
        }
        public void Append(ScanRow row, CycleIndexTracker cycle, PeakScratch scratch)
        {
            masses.Append(); intensities.Append();
            scratch.Append(row, (FloatArray.Builder)masses.ValueBuilder, (FloatArray.Builder)intensities.ValueBuilder);
            headers.Append(row.Header); scans.Append(row.ScanNumber); packets.Append(row.PacketType);
            baseMz.Append(row.BasePeakMz); baseIntensity.Append(row.BasePeakIntensity); retention.Append(row.RetentionTime);
            if (row.HasFillTimeMs) fill.Append(row.FillTimeMs); else fill.AppendNull();
            low.Append(row.LowMz); high.Append(row.HighMz); tic.Append(row.Tic);
            if (row.MsOrder > 1)
            {
                center.Append(row.CenterMz); width.Append(row.IsolationWidthMz); energy.Append(row.CollisionEnergy);
                if (row.HasCollisionEnergyEv) ev.Append(row.CollisionEnergyEv); else ev.AppendNull();
            }
            else { center.AppendNull(); width.AppendNull(); energy.AppendNull(); ev.AppendNull(); }
            order.Append(row.MsOrder);
            cycles.Append(cycle.GetCycleIndex(row.MsOrder, row.CenterMz, row.IsolationWidthMz));
            Count++;
            EstimatedBytes += row.ArrowBytes;
        }
        public RecordBatch Build(Schema schema)
        {
            var arrays = new List<IArrowArray>();
            try
            {
                arrays.Add(masses.Build()); arrays.Add(intensities.Build()); arrays.Add(headers.Build());
                arrays.Add(scans.Build()); arrays.Add(baseMz.Build()); arrays.Add(baseIntensity.Build());
                arrays.Add(packets.Build()); arrays.Add(retention.Build()); arrays.Add(fill.Build());
                arrays.Add(low.Build()); arrays.Add(high.Build()); arrays.Add(tic.Build()); arrays.Add(center.Build());
                arrays.Add(width.Build()); arrays.Add(energy.Build()); arrays.Add(ev.Build()); arrays.Add(order.Build()); arrays.Add(cycles.Build());
                return new RecordBatch(schema, arrays, Count);
            }
            catch { foreach (var array in arrays) array.Dispose(); throw; }
        }
    }

    sealed class CycleIndexTracker
    {
        private const float IsolationWindowToleranceMz = 1.0e-3f;
        private int cycleIndex = 1;
        private bool hasFirstMs2Window = false;
        private float firstMs2CenterMz = 0.0f;
        private float firstMs2IsolationWidthMz = 0.0f;

        public int GetCycleIndex(byte msOrder, float centerMz, float isolationWidthMz)
        {
            if (msOrder != 2)
            {
                return cycleIndex;
            }

            if (!hasFirstMs2Window)
            {
                hasFirstMs2Window = true;
                firstMs2CenterMz = centerMz;
                firstMs2IsolationWidthMz = isolationWidthMz;
                return cycleIndex;
            }

            if (IsFirstMs2Window(centerMz, isolationWidthMz))
            {
                cycleIndex++;
            }

            return cycleIndex;
        }

        private bool IsFirstMs2Window(float centerMz, float isolationWidthMz)
        {
            return Math.Abs(centerMz - firstMs2CenterMz) <= IsolationWindowToleranceMz &&
                   Math.Abs(isolationWidthMz - firstMs2IsolationWidthMz) <= IsolationWindowToleranceMz;
        }
    }

    sealed class ScanReaderWorker : IDisposable
    {
        public IRawDataPlus RawFile { get; }
        public int HcdEnergyFieldIndex = -2;
        public int FillTimeFieldIndex = -2;

        private ScanReaderWorker(IRawDataPlus rawFile)
        {
            RawFile = rawFile;
            try { RawFile.SelectInstrument(Device.MS, 1); }
            catch { RawFile.Dispose(); throw; }
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

    static void DisposeScanReaders(List<ScanReaderWorker>? workers, IRawFileThreadManager? manager)
    {
        List<Exception>? failures = null;
        if (workers != null)
            foreach (var worker in workers)
            {
                try { worker.Dispose(); }
                catch (Exception ex) { (failures ??= new()).Add(ex); }
            }
        try { manager?.Dispose(); }
        catch (Exception ex) { (failures ??= new()).Add(ex); }
        if (failures != null) throw new AggregateException("Failed to release RAW readers.", failures);
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
            DisposeScanReaders(workers, null);
            throw;
        }

        return workers;
    }

    static ScanRow ReadScanRow(IRawDataPlus rawFile, int scanNumber, ref int hcdEnergyFieldIndex, ref int fillTimeFieldIndex)
    {
        var stats = rawFile.GetScanStatsForScanNumber(scanNumber);
        // This is the SDK's mass/intensity-only view of the same label stream
        // returned by GetCentroidStream. It honors IncludeReferenceAndExceptionData
        // but avoids allocating flags Pioneer never reads. SdkSemantics verifies
        // both inclusion settings bitwise against Scan.FromFile and the full stream.
        var centroid = rawFile.GetSimplifiedCentroids(scanNumber);
        var masses = centroid.Masses ?? System.Array.Empty<double>();
        var intensities = centroid.Intensities ?? System.Array.Empty<double>();
        int centroidLength = masses.Length;
        if (intensities.Length != centroidLength)
            throw new InvalidDataException($"Centroid mass/intensity length mismatch at scan {scanNumber}.");
        var row = new ScanRow
        {
            ScanNumber = scanNumber,
            Masses = masses,
            Intensities = intensities,
            Length = centroidLength,
            Header = rawFile.GetFilterForScanNumber(scanNumber).ToString(),
            BasePeakMz = (float)stats.BasePeakMass,
            BasePeakIntensity = (float)stats.BasePeakIntensity,
            PacketType = stats.PacketType,
            RetentionTime = (float)stats.StartTime,
            LowMz = (float)stats.LowMass,
            HighMz = (float)stats.HighMass,
            Tic = (float)stats.TIC
        };
        var scanEvent = rawFile.GetScanEventForScanNumber(scanNumber);
        row.MsOrder = (byte)scanEvent.MSOrder;
        var trailerData = rawFile.GetTrailerExtraInformation(scanNumber);
        row.HasFillTimeMs = TryReadFillTimeMs(trailerData, ref fillTimeFieldIndex, out row.FillTimeMs);
        if (row.MsOrder > 1)
        {
            row.CenterMz = (float)scanEvent.GetMass(0);
            row.IsolationWidthMz = (float)scanEvent.GetIsolationWidth(0) + (float)scanEvent.GetIsolationWidthOffset(0);
            row.CollisionEnergy = (float)scanEvent.GetEnergy(0);
            if (TryResolveHcdEnergyFieldIndex(trailerData.Labels, trailerData.Length, ref hcdEnergyFieldIndex))
                row.HasCollisionEnergyEv = TryParseCollisionEnergyEv(trailerData.Values[hcdEnergyFieldIndex].Trim(), out row.CollisionEnergyEv);
        }
        return row;
    }

    static bool TryReadFillTimeMs(ILogEntryAccess trailerData, ref int fillTimeFieldIndex, out float fillTimeMs)
    {
        fillTimeMs = 0.0f;
        if (!TryResolveFillTimeFieldIndex(trailerData.Labels, trailerData.Length, ref fillTimeFieldIndex))
        {
            return false;
        }

        if (fillTimeFieldIndex >= trailerData.Values.Length)
        {
            return false;
        }

        string fillTimeValue = trailerData.Values[fillTimeFieldIndex].Trim();
        return TryParseFloat(fillTimeValue, out fillTimeMs);
    }

    static bool TryResolveFillTimeFieldIndex(IReadOnlyList<string> labels, int trailerLength, ref int fillTimeFieldIndex)
    {
        int labelCount = Math.Min(trailerLength, labels.Count);
        if (fillTimeFieldIndex >= 0 &&
            fillTimeFieldIndex < labelCount &&
            IsFillTimeTrailerLabel(labels[fillTimeFieldIndex]))
        {
            return true;
        }

        for (int j = 0; j < labelCount; j++)
        {
            if (IsFillTimeTrailerLabel(labels[j]))
            {
                fillTimeFieldIndex = j;
                return true;
            }
        }

        fillTimeFieldIndex = -1;
        return false;
    }

    static bool IsFillTimeTrailerLabel(string label) =>
        label.Contains(IonInjectionTimeTrailerLabelFragment, StringComparison.OrdinalIgnoreCase);

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
                if (TryParseFloat(value.Trim(), out float parsedValue))
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

        return TryParseFloat(energyValue, out ev);
    }

    static bool TryParseFloat(string value, out float parsedValue)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedValue) ||
               float.TryParse(value, out parsedValue);
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
