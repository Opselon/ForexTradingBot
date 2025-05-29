
using TL;

namespace Application.Features.Forwarding.Interfaces
{
    public class InputMediaWithCaption
    {
        public InputMedia Media { get; set; }
        public string? Caption { get; set; }
        public MessageEntity[]? Entities { get; set; }
    }
    public class MediaGroupBuffer
    {
        public List<InputMediaWithCaption> Items { get; } = new List<InputMediaWithCaption>();
        public CancellationTokenSource CancellationTokenSource { get; set; } = new CancellationTokenSource();
        public long PeerId { get; set; }
        public int ReplyToMsgId { get; set; }
        public Peer? SenderPeer { get; set; }
        public long OriginalMessageId { get; set; }
    }
}