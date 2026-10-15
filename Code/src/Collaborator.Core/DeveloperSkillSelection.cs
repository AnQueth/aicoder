namespace Collaborator.Core;

/// <summary>Identifies a user-configured specialist without choosing its active revision.</summary>
public sealed record DeveloperSkillSelection(string RoleId, string Domain, string SkillId);

/// <summary>Resolves requested specialists against enabled, revisioned registry entries.</summary>
public static class DeveloperSkillSelectionPolicy
{
    /// <summary>Returns a stable order for request comparison; null means use the enabled registry.</summary>
    public static DeveloperSkillSelection[]? Normalize(DeveloperSkillSelection[]? requested) =>
        requested?.OrderBy(skill => skill.RoleId, StringComparer.Ordinal)
            .ThenBy(skill => skill.Domain, StringComparer.Ordinal)
            .ThenBy(skill => skill.SkillId, StringComparer.Ordinal).ToArray();

    /// <summary>Selects only requested enabled skills, or all enabled skills when selection is omitted.</summary>
    public static bool TryResolve(
        DeveloperSkillSnapshot[] enabledSkills, DeveloperSkillSelection[]? requested,
        out DeveloperSkillSnapshot[] selected, out string error)
    {
        error = string.Empty;
        if (requested is null)
        {
            selected = enabledSkills;
            return true;
        }
        var byKey = enabledSkills.ToDictionary(skill =>
            new DeveloperSkillSelection(skill.Skill.RoleId, skill.Skill.Domain, skill.Skill.SkillId));
        var result = new List<DeveloperSkillSnapshot>();
        foreach (var key in Normalize(requested)!)
        {
            if (!byKey.TryGetValue(key, out var skill))
            {
                selected = [];
                error = $"Selected specialist '{key.RoleId}/{key.Domain}/{key.SkillId}' is missing, disabled, or has no active revision.";
                return false;
            }
            result.Add(skill);
        }
        selected = result.ToArray();
        return true;
    }
}
