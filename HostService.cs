using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NetCord.Rest;
namespace GreatLoveImmortalVenerable;
public sealed class HostService(BotSettings settings)
{
    public async Task<JsonElement> Request(string action,string? service=null)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(settings.SocketPath),timeout.Token);
        using var stream=new NetworkStream(socket);
        using var writer=new StreamWriter(stream,new UTF8Encoding(false),leaveOpen:true) {AutoFlush=true};
        await writer.WriteLineAsync(JsonSerializer.Serialize(new{action,service}).AsMemory(),timeout.Token);
        using var reader=new StreamReader(stream);
        var text=await reader.ReadLineAsync(timeout.Token)??throw new IOException("Host service did not reply.");
        using var doc=JsonDocument.Parse(text);
        if(!doc.RootElement.GetProperty("ok").GetBoolean())throw new InvalidOperationException("Host request was refused or failed. Check the local service logs.");
        return doc.RootElement.GetProperty("data").Clone();
    }
    public async Task<EmbedProperties> Status(string action,string? service=null)
    {
        var data=await Request(action,service);
        var lines=data.ValueKind==JsonValueKind.Array?data.EnumerateArray().Select(x=>x.ToString()):data.EnumerateObject().Select(x=>$"**{x.Name}**: {x.Value}");
        return Embeds.Build("Home server · "+action,AnimeService.Trim(string.Join("\n",lines),3500));
    }
}
