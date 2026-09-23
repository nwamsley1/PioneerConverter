using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

// Compare logical values across arbitrary IPC record-batch boundaries. Float bits,
// nulls, list lengths, field order, types, nullability and metadata must all match.
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: ArrowCompare BASELINE.arrow CANDIDATE.arrow");
    return 2;
}
try
{
    using var expected = new Rows(args[0]);
    using var actual = new Rows(args[1]);
    CompareSchema(expected.Schema, actual.Schema);
    long rows = 0, peakValues = 0;
    while (true)
    {
        bool left = expected.MoveNext(), right = actual.MoveNext();
        if (left != right) throw new Exception($"Row count differs at row {rows}");
        if (!left) break;
        for (int column = 0; column < expected.Schema.FieldsList.Count; column++)
            CompareValue(expected.Batch!.Column(column), expected.Index,
                actual.Batch!.Column(column), actual.Index,
                $"row {rows}, column {expected.Schema.FieldsList[column].Name}", ref peakValues);
        rows++;
    }
    Console.WriteLine($"PASS: {rows} rows, {expected.Schema.FieldsList.Count} columns, {peakValues} list elements match exactly (including float bits and nulls).");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex.Message}");
    return 1;
}

static void CompareSchema(Schema expected, Schema actual)
{
    CompareMetadata(expected.Metadata, actual.Metadata, "schema");
    if (expected.FieldsList.Count != actual.FieldsList.Count) throw new Exception("Column count differs");
    for (int i = 0; i < expected.FieldsList.Count; i++) CompareField(expected.FieldsList[i], actual.FieldsList[i]);
}
static void CompareField(Field expected, Field actual)
{
    if (expected.Name != actual.Name || expected.IsNullable != actual.IsNullable || expected.DataType.TypeId != actual.DataType.TypeId)
        throw new Exception($"Schema differs: expected {expected}, got {actual}");
    CompareMetadata(expected.Metadata, actual.Metadata, expected.Name);
    if (expected.DataType is ListType left && actual.DataType is ListType right) CompareField(left.ValueField, right.ValueField);
}
static void CompareMetadata(IReadOnlyDictionary<string,string>? left, IReadOnlyDictionary<string,string>? right, string context)
{
    if ((left?.Count ?? 0) != (right?.Count ?? 0) || (left != null && left.Any(kv => right == null || !right.TryGetValue(kv.Key, out string? value) || value != kv.Value)))
        throw new Exception($"Metadata differs: {context}");
}
static void CompareValue(IArrowArray expected, int i, IArrowArray actual, int j, string context, ref long peakValues)
{
    if (expected.IsNull(i) != actual.IsNull(j)) throw new Exception($"Null differs: {context}");
    if (expected.IsNull(i)) return;
    bool equal = (expected, actual) switch
    {
        (FloatArray a, FloatArray b) => BitConverter.SingleToInt32Bits(a.GetValue(i)!.Value) == BitConverter.SingleToInt32Bits(b.GetValue(j)!.Value),
        (DoubleArray a, DoubleArray b) => BitConverter.DoubleToInt64Bits(a.GetValue(i)!.Value) == BitConverter.DoubleToInt64Bits(b.GetValue(j)!.Value),
        (Int32Array a, Int32Array b) => a.GetValue(i) == b.GetValue(j),
        (Int64Array a, Int64Array b) => a.GetValue(i) == b.GetValue(j),
        (UInt8Array a, UInt8Array b) => a.GetValue(i) == b.GetValue(j),
        (StringArray a, StringArray b) => a.GetString(i) == b.GetString(j),
        (ListArray, ListArray) => true,
        _ => throw new NotSupportedException($"Unhandled Arrow type at {context}: {expected.Data.DataType}")
    };
    if (!equal) throw new Exception($"Value differs: {context}");
    if (expected is ListArray left && actual is ListArray right)
    {
        int leftStart = left.ValueOffsets[i], rightStart = right.ValueOffsets[j];
        int count = left.ValueOffsets[i + 1] - leftStart;
        if (count != right.ValueOffsets[j + 1] - rightStart) throw new Exception($"List length differs: {context}");
        if (left.Values is FloatArray leftFloats && right.Values is FloatArray rightFloats)
        {
            for (int k = 0; k < count; k++)
            {
                int a = leftStart + k, b = rightStart + k;
                if (leftFloats.IsNull(a) != rightFloats.IsNull(b) ||
                    (!leftFloats.IsNull(a) && BitConverter.SingleToInt32Bits(leftFloats.GetValue(a)!.Value) !=
                        BitConverter.SingleToInt32Bits(rightFloats.GetValue(b)!.Value)))
                    throw new Exception($"Peak differs: {context}, element {k}");
            }
        }
        else
        {
            for (int k = 0; k < count; k++)
                CompareValue(left.Values, leftStart + k, right.Values, rightStart + k, $"{context}, element {k}", ref peakValues);
        }
        peakValues += count;
    }
}
sealed class Rows : IDisposable
{
    readonly FileStream stream;
    readonly ArrowFileReader reader;
    public Schema Schema { get; }
    public RecordBatch? Batch { get; private set; }
    public int Index { get; private set; } = -1;
    public Rows(string path)
    {
        stream = File.OpenRead(path);
        reader = new ArrowFileReader(stream);
        Schema = reader.Schema;
    }
    public bool MoveNext()
    {
        Index++;
        while (Batch == null || Index >= Batch.Length)
        {
            Batch?.Dispose();
            Batch = reader.ReadNextRecordBatch();
            Index = 0;
            if (Batch == null) return false;
        }
        return true;
    }
    public void Dispose() { Batch?.Dispose(); reader.Dispose(); stream.Dispose(); }
}
