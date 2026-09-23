#!/usr/bin/env python3
"""Copy an unmodified pre-pipeline checkout and add stage timers (scan-workers > 1).

Copies code/libraries only, never RAW data. Fails if expected old source differs.
The first checkout's uncommitted Program.cs changes are preserved in Program.cs.original.
"""
import argparse
import pathlib
import shutil

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('source', type=pathlib.Path)
parser.add_argument('destination', type=pathlib.Path)
args = parser.parse_args()
source = args.source.resolve()
destination = args.destination.resolve()
s = (source / 'Program.cs').read_text(encoding='utf-8-sig')
if 'Scan.FromFile' not in s or 'BoundedPipelineQueue' in s:
    raise SystemExit('Expected pre-pipeline implementation with Scan.FromFile')
def replace(old, new):
    global s
    if s.count(old) != 1:
        raise SystemExit(f'Expected exactly one baseline marker: {old[:90]!r}; source layout changed')
    s = s.replace(old, new)
replace('int hcdEnergyFieldIndex = -2;', 'long baselineStatsTicks = 0, baselineExtractionTicks = 0, baselineBuildTicks = 0, baselineWritingTicks = 0;\n        int hcdEnergyFieldIndex = -2;')
replace('                for (int rowIndex = 0; rowIndex < batchRowCount; rowIndex++)\n', '                long statsStarted = Stopwatch.GetTimestamp();\n                for (int rowIndex = 0; rowIndex < batchRowCount; rowIndex++)\n')
replace('                massListBuilder.Reserve(batchRowCount);', '                baselineStatsTicks += Stopwatch.GetTimestamp() - statsStarted;\n                massListBuilder.Reserve(batchRowCount);')
replace('                        Parallel.For(\n', '                        long extractionStarted = Stopwatch.GetTimestamp();\n                        Parallel.For(\n')
replace('                            });\n\n                        for (int localIndex', '                            });\n                        baselineExtractionTicks += Stopwatch.GetTimestamp() - extractionStarted;\n\n                        for (int localIndex')
replace('                writer.WriteRecordBatch(BuildRecordBatch(batchStart));', '                long buildStarted = Stopwatch.GetTimestamp();\n                var builtBatch = BuildRecordBatch(batchStart);\n                baselineBuildTicks += Stopwatch.GetTimestamp() - buildStarted;\n                long writingStarted = Stopwatch.GetTimestamp();\n                writer.WriteRecordBatch(builtBatch);\n                baselineWritingTicks += Stopwatch.GetTimestamp() - writingStarted;')
replace('        watch.Stop();\n        Console.WriteLine("Execution Time:', '        watch.Stop();\n        double msPerTick = 1000.0 / Stopwatch.Frequency;\n        Console.WriteLine($"PERF statistics_ms={baselineStatsTicks * msPerTick:F3} extraction_ms={(baselineStatsTicks + baselineExtractionTicks) * msPerTick:F3} assembly_ms={(baselineBuildTicks - baselineStatsTicks - baselineExtractionTicks) * msPerTick:F3} writing_ms={baselineWritingTicks * msPerTick:F3} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3}");\n        Console.WriteLine("Execution Time:')
destination.mkdir(parents=True, exist_ok=False)
shutil.copy2(source / 'Program.cs', destination / 'Program.cs.original')
shutil.copy2(source / 'PioneerConverter.csproj', destination)
shutil.copytree(source / 'Libs', destination / 'Libs')
(destination / 'Program.cs').write_text(s)
print(f'Prepared {destination}. Build with dotnet build -c Release. Stage separation requires --threads-per-file > 1.')
print('Extraction includes the statistics pass; assembly = batch build - statistics - parallel extraction; writing times WriteRecordBatch only.')
