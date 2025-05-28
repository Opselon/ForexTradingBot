
using TL;

namespace Application.Features.Forwarding.Interfaces
{
    public class InputMediaWithCaption
    {
        public InputMedia Media { get; set; }
        public string? Caption { get; set; }
        public TL.MessageEntity[]? Entities { get; set; }
    }
}