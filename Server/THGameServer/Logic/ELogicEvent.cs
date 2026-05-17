namespace TH.Server.Logic;

[Flags]
public enum ELogicEvent : byte
{
    None    = 0,
    Prepare = 1 << 0,
    Arrange = 1 << 1,
}
