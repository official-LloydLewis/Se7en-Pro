using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsiphonUI.Models;

namespace PsiphonUI.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly string _path;
    private readonly string _legacyPath;

    public UserSettings Settings { get; private set; } = new();

    public event EventHandler? SettingsChanged;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppBrand.SafeName);
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        _legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Psiphon",
            "settings.json");
    }

    public void Load()
    {
        try
        {
            var loadPath = File.Exists(_path) ? _path : _legacyPath;
            if (!File.Exists(loadPath))
            {
                Settings = new UserSettings();
                Save();
                return;
            }

            var json = File.ReadAllText(loadPath);
            Settings = JsonSerializer.Deserialize<UserSettings>(json, JsonOpts) ?? new UserSettings();
            if (!string.Equals(loadPath, _path, StringComparison.OrdinalIgnoreCase))
            {
                Save();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from {Path}; using defaults", _path);
            Settings = new UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, JsonOpts);
            File.WriteAllText(_path, json);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _path);
        }
    }
}
