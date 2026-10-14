using System.Globalization;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;
namespace GreatLoveImmortalVenerable;
public static class CalendarUi
{
    public static DateOnly ParseDate(string? value)
    {
        var today=DateOnly.FromDateTime(DateTime.UtcNow);
        if(string.IsNullOrWhiteSpace(value))return today;
        if(!DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)||Math.Abs(day.DayNumber-today.DayNumber)>30)throw new ArgumentException("Invalid calendar date.");
        return day;
    }
    public static ActionRowProperties Buttons()=>new([
        new ButtonProperties("gliv-previous","Previous",ButtonStyle.Secondary),
        new ButtonProperties("gliv-today","Today",ButtonStyle.Primary),
        new ButtonProperties("gliv-next","Next",ButtonStyle.Secondary)]);
}
public sealed class CalendarButtons(AnimeService anime,BotSettings settings):ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("gliv-previous")]public Task Previous()=>Navigate(-1);
    [ComponentInteraction("gliv-next")]public Task Next()=>Navigate(1);
    [ComponentInteraction("gliv-today")]public Task Today()=>Navigate(0);
    private async Task Navigate(int change)
    {
        if(Context.Interaction.GuildId!=settings.GuildId) {
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties {Content="This calendar is unavailable here.",Flags=MessageFlags.Ephemeral}));return;
        }
        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredModifyMessage);
        try {
            var footer=Context.Interaction.Message.Embeds.FirstOrDefault()?.Footer?.Text;
            var day=change==0?CalendarUi.ParseDate(null):CalendarUi.ParseDate(footer?.Replace("Date: ","")).AddDays(change);
            CalendarUi.ParseDate(day.ToString("yyyy-MM-dd"));
            var embed=await anime.Calendar(day);
            await Context.Interaction.ModifyResponseAsync(p=>{p.Embeds=[embed];p.Components=[CalendarUi.Buttons()];});
        } catch(Exception e) {Console.Error.WriteLine($"Calendar button failed: {e.GetType().Name}");await Context.Interaction.SendFollowupMessageAsync(new InteractionMessageProperties {Content="Calendar unavailable or date outside the 30-day range. Try /calendar again.",Flags=MessageFlags.Ephemeral});}
    }
}
