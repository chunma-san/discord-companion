using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
namespace GreatLoveImmortalVenerable;
public static class NotificationPreview
{
    public static async Task SendReading(BotSettings settings)
    {
        var store=new ReadingStore(Environment.GetEnvironmentVariable("READING_DATA_DIR")??"/app/data");using var feeds=new ReadingFeeds();
        using var http=new HttpClient{BaseAddress=new Uri("https://discord.com/api/v10/")};http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bot",settings.Token);
        foreach(var watch in store.Watches)
        {
            try {
                ChapterEntry entry;var cached=false;
                try{entry=(await feeds.Fetch(watch.FeedUrl)).First();}
                catch(SourceVerificationException)when(watch.Latest.Count>0){entry=watch.Latest.First();cached=true;}
                using var channel=await http.GetAsync("channels/"+watch.ChannelId);channel.EnsureSuccessStatusCode();using var channelData=JsonDocument.Parse(await channel.Content.ReadAsStringAsync());
                if(channelData.RootElement.GetProperty("guild_id").GetString()!=settings.GuildId.ToString())throw new InvalidOperationException("Unexpected destination guild");
                var embed=new Dictionary<string,object>{["title"]=entry.Title,["url"]=entry.Url,["color"]=13280328,["description"]="TEST • "+watch.Name+(cached?"\nLast successful cached listing; publisher currently requests verification.":"\nLatest public chapter listing."),["footer"]=new{text="Preview only · notification history unchanged"}};
                if(entry.CoverUrl is not null)embed["thumbnail"]=new{url=entry.CoverUrl};
                using var response=await http.PostAsJsonAsync("channels/"+watch.ChannelId+"/messages",new{allowed_mentions=new{parse=Array.Empty<string>()},embeds=new[]{embed}});response.EnsureSuccessStatusCode();
                using var result=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Console.WriteLine(JsonSerializer.Serialize(new{source=watch.Name,status="sent",cached,id=result.RootElement.GetProperty("id").GetString()}));
            }catch(Exception error){Console.WriteLine(JsonSerializer.Serialize(new{source=watch.Name,status="failed",error=error.GetType().Name}));}
            await Task.Delay(1500);
        }
    }
}
