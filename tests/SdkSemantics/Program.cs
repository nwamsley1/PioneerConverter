using System.Reflection;
using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Types;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoFisher.CommonCore.RawFileReader;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: SdkSemantics RAW_PATH [RAW_PATH ...]");
    return 2;
}

try
{
    CheckEmptyCentroidAssembly();
    foreach (string path in args) CheckCentroidStreams(path);
    return 0;
}
catch (Exception ex)
{
    while (ex is TargetInvocationException { InnerException: not null }) ex = ex.InnerException;
    Console.Error.WriteLine($"FAIL: {ex.Message}");
    return 1;
}

static void CheckCentroidStreams(string path)
{
    using var raw = RawFileReaderAdapter.FileFactory(path);
    if (!raw.IsOpen || raw.IsError) throw new IOException($"Unable to read RAW file: {path}");
    raw.SelectInstrument(Device.MS, 1);
    long comparedPeaks = 0;
    foreach (bool include in new[] { false, true })
    {
        raw.IncludeReferenceAndExceptionData = include;
        long directAllocated = 0;
        long simplifiedAllocated = 0;
        int simplifiedEmptyRepresentationDifferences = 0;
        for (int scanNumber = raw.RunHeaderEx.FirstSpectrum; scanNumber <= raw.RunHeaderEx.LastSpectrum; scanNumber++)
        {
            string context = $"{path}, scan {scanNumber}, IncludeReferenceAndExceptionData={include}";
            var legacy = Scan.FromFile(raw, scanNumber)?.CentroidScan;
            long beforeDirect = GC.GetAllocatedBytesForCurrentThread();
            var direct = raw.GetCentroidStream(scanNumber, raw.IncludeReferenceAndExceptionData);
            directAllocated += GC.GetAllocatedBytesForCurrentThread() - beforeDirect;
            Require((legacy == null) == (direct == null), $"Stream nullness differs: {context}");
            if (legacy == null) continue;
            Require(legacy.Length == direct!.Length, $"Centroid length differs: {context}");
            CompareDoubles(legacy.Masses, direct.Masses, $"masses: {context}");
            CompareDoubles(legacy.Intensities, direct.Intensities, $"intensities: {context}");
            long beforeSimplified = GC.GetAllocatedBytesForCurrentThread();
            var simplified = raw.GetSimplifiedCentroids(scanNumber);
            simplifiedAllocated += GC.GetAllocatedBytesForCurrentThread() - beforeSimplified;
            Require(simplified != null, $"Simplified stream is null: {context}");
            CompareSimplifiedDoubles(direct.Masses, simplified!.Masses, $"simplified masses: {context}",
                ref simplifiedEmptyRepresentationDifferences);
            CompareSimplifiedDoubles(direct.Intensities, simplified.Intensities, $"simplified intensities: {context}",
                ref simplifiedEmptyRepresentationDifferences);
            comparedPeaks += legacy.Length;
        }
        Console.WriteLine($"  IncludeReferenceAndExceptionData={include}: direct allocated {directAllocated:N0} bytes; simplified allocated {simplifiedAllocated:N0} bytes; null/empty representation differences {simplifiedEmptyRepresentationDifferences:N0}.");
    }
    int scans = raw.RunHeaderEx.LastSpectrum - raw.RunHeaderEx.FirstSpectrum + 1;
    CheckTrailerScalars(raw, path);
    Console.WriteLine($"PASS: {path}: {scans} scans, both inclusion settings, {comparedPeaks} peak pairs bitwise identical.");
}

static void CheckTrailerScalars(IRawDataExtended raw, string path)
{
    const string hcdLabel = "HCD Energy V:";
    var headers = raw.GetTrailerExtraHeaderInformation();
    int fillIndex = System.Array.FindIndex(headers, h => h.Label.Contains("ion injection time", StringComparison.OrdinalIgnoreCase));
    int hcdIndex = System.Array.FindIndex(headers, h => h.Label == hcdLabel);
    Require(fillIndex >= 0, $"No injection-time trailer header: {path}");
    int fillField = fillIndex;
    int hcdField = hcdIndex;
    long fullAllocated = 0, scalarAllocated = 0, valuesAllocated = 0;
    int comparedFill = 0, comparedHcd = 0;
    for (int scan = raw.RunHeaderEx.FirstSpectrum; scan <= raw.RunHeaderEx.LastSpectrum; scan++)
    {
        long beforeFull = GC.GetAllocatedBytesForCurrentThread();
        var full = raw.GetTrailerExtraInformation(scan);
        fullAllocated += GC.GetAllocatedBytesForCurrentThread() - beforeFull;
        long beforeValues = GC.GetAllocatedBytesForCurrentThread();
        object[] values = raw.GetTrailerExtraValues(scan);
        valuesAllocated += GC.GetAllocatedBytesForCurrentThread() - beforeValues;
        int formattedFill = full.Labels.ToList().FindIndex(label =>
            label.Contains("ion injection time", StringComparison.OrdinalIgnoreCase));
        if (formattedFill >= 0 && formattedFill < full.Length && formattedFill < full.Values.Length)
        {
            bool expectedPresent = TryParseFloat(full.Values[formattedFill], out float expected);
            long beforeScalar = GC.GetAllocatedBytesForCurrentThread();
            object scalar = raw.GetTrailerExtraValue(scan, fillField);
            scalarAllocated += GC.GetAllocatedBytesForCurrentThread() - beforeScalar;
            bool actualPresent = TryParseFloat(FormatTrailerValue(headers[fillIndex], scalar), out float actual);
            Require(expectedPresent == actualPresent, $"Injection-time presence differs: {path}, scan {scan}");
            if (expectedPresent)
                Require(BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
                    $"Injection-time value differs: {path}, scan {scan}, formatted={full.Values[formattedFill]}, scalar={scalar}");
            Require(fillIndex < values.Length, $"Unformatted injection-time index missing: {path}, scan {scan}");
            bool valuesPresent = TryParseFloat(FormatTrailerValue(headers[fillIndex], values[fillIndex]), out float valuesValue);
            Require(expectedPresent == valuesPresent && (!expectedPresent ||
                    BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(valuesValue)),
                $"Unformatted injection-time value differs: {path}, scan {scan}");
            comparedFill++;
        }
        int formattedHcd = full.Labels.ToList().FindIndex(label => label == hcdLabel);
        if (hcdField >= 0 && formattedHcd >= 0 && formattedHcd < full.Length && formattedHcd < full.Values.Length &&
            (int)raw.GetScanEventForScanNumber(scan).MSOrder > 1)
        {
            bool expectedPresent = TryEnergy(full.Values[formattedHcd], out float expected);
            long beforeScalar = GC.GetAllocatedBytesForCurrentThread();
            object scalar = raw.GetTrailerExtraValue(scan, hcdField);
            scalarAllocated += GC.GetAllocatedBytesForCurrentThread() - beforeScalar;
            bool actualPresent = TryEnergy(FormatTrailerValue(headers[hcdIndex], scalar), out float parsed);
            float actual = parsed;
            Require(expectedPresent == actualPresent, $"HCD-energy presence differs: {path}, scan {scan}");
            if (expectedPresent)
                Require(BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
                    $"HCD-energy value differs: {path}, scan {scan}, formatted={full.Values[formattedHcd]}, scalar={scalar}");
            Require(hcdIndex < values.Length, $"Unformatted HCD index missing: {path}, scan {scan}");
            bool valuesPresent = TryEnergy(FormatTrailerValue(headers[hcdIndex], values[hcdIndex]), out float valuesValue);
            Require(expectedPresent == valuesPresent && (!expectedPresent ||
                    BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(valuesValue)),
                $"Unformatted HCD value differs: {path}, scan {scan}");
            comparedHcd++;
        }
    }
    Console.WriteLine($"  Trailer equivalence: fill={comparedFill:N0}, HCD={comparedHcd:N0}; full allocated {fullAllocated:N0} bytes; scalar allocated {scalarAllocated:N0} bytes; unformatted values allocated {valuesAllocated:N0} bytes.");
}

static string FormatTrailerValue(HeaderItem header, object? value)
{
    string raw = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    return header.FormatValue(raw);
}

static bool TryEnergy(string value, out float result)
{
    var parts = value.Split(',');
    float sum = 0;
    int count = 0;
    foreach (string part in parts)
        if (TryParseFloat(part, out float parsed)) { sum += parsed; count++; }
    result = count == 0 ? 0 : sum / count;
    return count != 0;
}

static bool TryParseFloat(string value, out float result) =>
    float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
    float.TryParse(value.Trim(), out result);

static void CompareSimplifiedDoubles(double[]? expected, double[]? actual, string context, ref int emptyRepresentationDifferences)
{
    if (expected == null || actual == null)
    {
        Require((expected?.Length ?? 0) == 0 && (actual?.Length ?? 0) == 0,
            $"Populated array became null: {context}");
        if ((expected == null) != (actual == null)) emptyRepresentationDifferences++;
        return;
    }
    CompareDoubles(expected, actual, context);
}

static void CompareDoubles(double[]? expected, double[]? actual, string context)
{
    Require((expected == null) == (actual == null), $"Array nullness differs: {context}");
    if (expected == null) return;
    Require(expected.Length == actual!.Length, $"Array length differs: {context}");
    for (int i = 0; i < expected.Length; i++)
        if (BitConverter.DoubleToInt64Bits(expected[i]) != BitConverter.DoubleToInt64Bits(actual[i]))
            throw new InvalidDataException($"Double value differs at peak {i}: {context}");
}

// Empty SDK centroid streams can have null arrays. Exercise production assembly
// code with empty/populated/empty rows to catch dereferences and stale scratch data.
// Reflection keeps this test from widening the production API for private classes.
static void CheckEmptyCentroidAssembly()
{
    var program = typeof(Options).Assembly.GetType("Program", throwOnError: true)!;
    Type Nested(string name) => program.GetNestedType(name, BindingFlags.NonPublic)
        ?? throw new MissingMemberException(program.FullName, name);
    var rowType = Nested("ScanRow");
    var builderType = Nested("ArrowBatchBuilder");
    var builder = Activator.CreateInstance(builderType, new object[] { 3 })!;
    var cycle = Activator.CreateInstance(Nested("CycleIndexTracker"), nonPublic: true)!;
    using var scratch = (IDisposable)Activator.CreateInstance(Nested("PeakScratch"), nonPublic: true)!;
    var append = builderType.GetMethod("Append")!;
    for (int i = 0; i < 3; i++)
    {
        var row = Activator.CreateInstance(rowType, nonPublic: true)!;
        rowType.GetField("ScanNumber")!.SetValue(row, i + 1);
        rowType.GetField("MsOrder")!.SetValue(row, (byte)1);
        rowType.GetField("Header")!.SetValue(row, "synthetic centroid regression");
        if (i == 1)
        {
            rowType.GetField("Masses")!.SetValue(row, new[] { 401.123456 });
            rowType.GetField("Intensities")!.SetValue(row, new[] { 987.654321 });
            rowType.GetField("Length")!.SetValue(row, 1);
        }
        Require((long)rowType.GetProperty("DecodedBytes")!.GetValue(row)! > 0, "Invalid decoded-byte estimate for empty centroid");
        append.Invoke(builder, new[] { row, cycle, scratch });
    }

    // A minimal schema suffices for this private builder test. ArrowCompare
    // separately verifies the public schema against the baseline.
    var fieldNames = new[] { "masses", "intensities", "headers", "scans", "baseMz", "baseIntensity",
        "packets", "retention", "fill", "low", "high", "tic", "center", "width", "energy", "ev", "order", "cycles" };
    var fieldTypes = new IArrowType[] { new ListType(FloatType.Default), new ListType(FloatType.Default), StringType.Default,
        Int32Type.Default, FloatType.Default, FloatType.Default, Int32Type.Default, FloatType.Default, FloatType.Default,
        FloatType.Default, FloatType.Default, FloatType.Default, FloatType.Default, FloatType.Default, FloatType.Default,
        FloatType.Default, UInt8Type.Default, Int32Type.Default };
    var schema = new Schema.Builder();
    for (int i = 0; i < fieldNames.Length; i++) schema.Field(new Field(fieldNames[i], fieldTypes[i], true));
    using var batch = (RecordBatch)builderType.GetMethod("Build")!.Invoke(builder, new object[] { schema.Build() })!;
    Require(batch.Length == 3, "Empty centroid rows were lost");
    for (int column = 0; column < 2; column++)
    {
        var list = (ListArray)batch.Column(column);
        Require(list.NullCount == 0 && list.ValueOffsets.SequenceEqual(new[] { 0, 0, 1, 1 }), "Empty centroid became null or retained stale peaks");
        var values = (FloatArray)list.Values;
        float expected = column == 0 ? (float)401.123456 : (float)987.654321;
        Require(values.Length == 1 && BitConverter.SingleToInt32Bits(values.GetValue(0)!.Value) == BitConverter.SingleToInt32Bits(expected),
            "Scratch conversion changed a populated peak between empty scans");
    }
    Console.WriteLine("PASS: null arrays with zero-length centroids assemble as non-null empty lists around a populated scan.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
