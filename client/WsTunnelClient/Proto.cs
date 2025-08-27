using ProtoBuf;

namespace WsTunnelClient
{
    public enum Type
    {
        PING = 0,
        PONG = 1,
        HELLO = 2,
        HELLO_OK = 3,
        OPEN = 4,
        OPENED = 5,
        DATA = 6,
        END = 7
    }

    [ProtoContract]
    public class Envelope
    {
        [ProtoMember(1)] public Type type { get; set; }
        [ProtoMember(2)] public string clientId { get; set; }
        [ProtoMember(3)] public string authHash { get; set; }
        [ProtoMember(4)] public string info { get; set; }
        [ProtoMember(5)] public string connId { get; set; }
        [ProtoMember(6)] public string host { get; set; }
        [ProtoMember(7)] public uint port { get; set; }
        [ProtoMember(8)] public byte[] data { get; set; }
        [ProtoMember(9)] public long t { get; set; }
    }
}