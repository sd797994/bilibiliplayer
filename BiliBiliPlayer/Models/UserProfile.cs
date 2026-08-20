namespace BiliBiliPlayer.Models;

public sealed class UserProfile
{
    public long Mid { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string AvatarUrl { get; set; } = string.Empty;

    public int Level { get; set; }

    public string Initial => string.IsNullOrWhiteSpace(UserName)
        ? "登"
        : UserName[..1].ToUpperInvariant();
}
