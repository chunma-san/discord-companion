namespace GreatLoveImmortalVenerable;

public sealed record BotSettings(string Token, ulong OwnerId, ulong GuildId, string SocketPath)
{
    public static BotSettings FromEnvironment() => new(
        Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? "",
        ulong.TryParse(Environment.GetEnvironmentVariable("OWNER_ID"), out var owner) ? owner : 0,
        ulong.TryParse(Environment.GetEnvironmentVariable("GUILD_ID"), out var guild) ? guild : 0,
        Environment.GetEnvironmentVariable("HOST_SOCKET") ?? "/run/gliv/host.sock");
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Token) || OwnerId == 0 || GuildId == 0)
            throw new InvalidOperationException("Configure DISCORD_TOKEN, OWNER_ID and GUILD_ID before starting.");
    }
    public bool IsOwner(ulong user, ulong? guild) => user == OwnerId && guild == GuildId;
}
