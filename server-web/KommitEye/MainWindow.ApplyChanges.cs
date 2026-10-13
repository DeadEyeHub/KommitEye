using KommitEye.Models;

namespace KommitEye;

public partial class MainWindow
{
    private readonly HashSet<string> _pendingSettings = [];
    private bool _applyingChanges;
    private void MarkSettingsChanged(string scope)
    {
        if (!_applyingChanges && !_switchingProject) _pendingSettings.Add(scope);
    }

    private void PersistModelProfile(ModelProfile profile)
    {
        var index = _models.Profiles.FindIndex(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) _models.Profiles.Add(profile); else _models.Profiles[index] = profile;
        _models.ActiveProfile = profile.Name;
        _configuration.SaveModels(_models);
        LoadProfiles(profile.Name);
        if (_configuration.WritesEnabled) _runner.UpdateProfile(profile);
    }
}
