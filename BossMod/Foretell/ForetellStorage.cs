namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private void StartStorageMaintenance()
    {
        _lastStorageMaintenance = DateTime.UtcNow;
        var rawDirectory = _rawDir;
        var replayDirectory = _replayDir;
        var protectedPaths = new[] { _rawPath, _raw.ActivePath, _replayPath };
        var retentionDays = _cfg.RecordingRetentionDays;
        var maximumBytes = Math.Clamp((long)_cfg.MaximumRecordingStorageGiB, 1, 1000) * 1024 * 1024 * 1024;
        _storageMaintenanceTask = Task.Run(() => ForetellStorageMaintenance.Run(rawDirectory, replayDirectory, protectedPaths,
            DateTime.UtcNow, retentionDays, maximumBytes));
    }

    private void PollStorageMaintenance()
    {
        var task = _storageMaintenanceTask;
        if (task == null || !task.IsCompleted) return;
        try { _lastStorageMaintenanceResult = task.GetAwaiter().GetResult(); }
        catch (Exception e) { _lastStorageMaintenanceResult = new() { CompletedAt = DateTime.UtcNow, Error = e.Message }; }
        _storageMaintenanceTask = null;
        _lastStorageRefresh = default;
    }
}
