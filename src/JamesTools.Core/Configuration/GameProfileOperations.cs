namespace JamesTools.Core.Configuration;

public static class GameProfileOperations
{
    /// <summary>Removes an unassigned profile. Returns a game-specific message when reassignment is required.</summary>
    public static bool TryRemove(ICollection<GameProfile> profiles, IEnumerable<GameRule> games, Guid profileId, out string message)
    {
        var assigned = games.Where(game => game.ProfileId == profileId).Select(game => game.Name).ToArray();
        if (assigned.Length > 0)
        {
            message = "Reassign these games first: " + string.Join(", ", assigned) + ".";
            return false;
        }
        var profile = profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            message = "The selected profile no longer exists.";
            return false;
        }
        profiles.Remove(profile);
        message = "";
        return true;
    }
}
