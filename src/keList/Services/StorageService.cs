using System.IO;
using System.Text.Json;
using KeList.Models;

namespace KeList.Services;

public sealed class StorageService
{
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "keList");

    private string LegacyDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TodoList");

    private string DataPath => Path.Combine(DataDirectory, "data.json");
    private string BackupPath => Path.Combine(DataDirectory, "data.backup.json");

    public AppData Load()
    {
        Directory.CreateDirectory(DataDirectory);
        MigrateLegacyDataIfNeeded();

        foreach (var path in new[] { DataPath, BackupPath })
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<AppData>(json, _jsonOptions);
                if (data is not null)
                {
                    return data;
                }
            }
            catch
            {
                // Try the backup before falling back to a clean data set.
            }
        }

        return new AppData();
    }

    private void MigrateLegacyDataIfNeeded()
    {
        if (File.Exists(DataPath) || !Directory.Exists(LegacyDataDirectory))
        {
            return;
        }

        foreach (var fileName in new[] { "data.json", "data.backup.json" })
        {
            var legacyPath = Path.Combine(LegacyDataDirectory, fileName);
            var destinationPath = Path.Combine(DataDirectory, fileName);

            if (File.Exists(legacyPath))
            {
                File.Copy(legacyPath, destinationPath, false);
            }
        }
    }

    public async Task SaveAsync(AppData data)
    {
        await _saveLock.WaitAsync();

        try
        {
            Directory.CreateDirectory(DataDirectory);
            var temporaryPath = Path.Combine(DataDirectory, $"data.{Guid.NewGuid():N}.tmp");
            var json = JsonSerializer.Serialize(data, _jsonOptions);

            await File.WriteAllTextAsync(temporaryPath, json);

            if (File.Exists(DataPath))
            {
                File.Copy(DataPath, BackupPath, true);
            }

            File.Move(temporaryPath, DataPath, true);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
