using System.Text.Json;
using BiliBiliPlayer.Models;

namespace BiliBiliPlayer.Services;

public static class BiliSessionStore
{
    private const string ProfileKey = "bili.user.profile.v1";

    public static UserProfile? LoadProfile()
    {
        try
        {
            var json = Preferences.Default.Get(ProfileKey, string.Empty);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<UserProfile>(json);
        }
        catch
        {
            Preferences.Default.Remove(ProfileKey);
            return null;
        }
    }

    public static void SaveProfile(UserProfile profile) =>
        Preferences.Default.Set(ProfileKey, JsonSerializer.Serialize(profile));

    public static void ClearProfile() => Preferences.Default.Remove(ProfileKey);
}
