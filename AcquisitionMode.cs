using System.Collections.Generic;

using ThermoFisher.CommonCore.Data;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;

/// <summary>
/// Records the run's acquisition mode as Arrow schema-level metadata, from the vendor's own
/// annotation only. For Thermo .raw files this is the scan filter's dependent flag (" d " in
/// the filter string; IScanEventBase.Dependent) on MS2 scans. A run is labelled "dda" when more than half of its MS2
/// scans are dependent; otherwise "unknown", and Pioneer classifies the file itself from the
/// data. A missing dependent flag is deliberately not taken to mean DIA: PRM, SureQuant
/// survey and calibration runs also lack it.
/// </summary>
internal sealed class AcquisitionModeInfo
{
    // Bump when the thresholds or decision logic change (Pioneer reads this).
    public const string RulesVersion = "1";

    public const string ModeKey = "acquisition_mode";
    public const string SourceKey = "acquisition_mode_source";
    public const string EvidenceKey = "acquisition_mode_evidence";
    public const string VersionKey = "acquisition_mode_version";
    public const string ConverterVersionKey = "converter_version";

    public int Ms2Scans { get; }
    public int DependentMs2Scans { get; }

    public string Mode => Ms2Scans > 0 && DependentMs2Scans > 0.5 * Ms2Scans ? "dda" : "unknown";
    public string Source => "vendor";
    public string Evidence => $"thermo dependent ms2 {DependentMs2Scans}/{Ms2Scans}";

    private AcquisitionModeInfo(int ms2Scans, int dependentMs2Scans)
    {
        Ms2Scans = ms2Scans;
        DependentMs2Scans = dependentMs2Scans;
    }

    /// <summary>
    /// Pass over scan events only (no peaks, trailers or scan statistics). The scan event
    /// carries the same MSOrder and Dependent values as the scan filter (the filter string is
    /// built from it) and is cheaper to read than GetFilterForScanNumber. The first pass on a
    /// file pays the SDK's one-time load of per-scan event data; pass a reader that the
    /// conversion also uses so that load is not paid twice.
    /// </summary>
    public static AcquisitionModeInfo FromScanEvents(IRawDataPlus rawFile, int firstScanNumber, int lastScanNumber,
        CancellationToken cancellationToken)
    {
        int ms2 = 0, dependent = 0;
        for (int scan = firstScanNumber; scan <= lastScanNumber; scan++)
        {
            if ((scan & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var scanEvent = rawFile.GetScanEventForScanNumber(scan);
            if (scanEvent.MSOrder != MSOrderType.Ms2) continue;
            ms2++;
            if (scanEvent.Dependent == TriState.On) dependent++;
        }
        return new AcquisitionModeInfo(ms2, dependent);
    }

    public IEnumerable<KeyValuePair<string, string>> Metadata()
    {
        yield return new KeyValuePair<string, string>(ModeKey, Mode);
        yield return new KeyValuePair<string, string>(SourceKey, Source);
        yield return new KeyValuePair<string, string>(EvidenceKey, Evidence);
        yield return new KeyValuePair<string, string>(VersionKey, RulesVersion);
        yield return new KeyValuePair<string, string>(ConverterVersionKey, $"{AppMetadata.AppName} {AppMetadata.Version}");
    }

    public override string ToString() => $"{ModeKey}={Mode} ({Source}: {Evidence})";
}
