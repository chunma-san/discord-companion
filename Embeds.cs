using NetCord;
using NetCord.Rest;
namespace GreatLoveImmortalVenerable;
public static class Embeds
{
    public static EmbedProperties Build(string title,string? description=null)=>new() {
        Title=AnimeService.Trim(title,256), Description=description is null?null:AnimeService.Trim(description,4000),
        Color=new Color(202,164,72),Footer=new EmbedFooterProperties {Text="Great Love Immortal Venerable"}
    };
}
