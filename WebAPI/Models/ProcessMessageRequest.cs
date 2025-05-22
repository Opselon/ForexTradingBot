namespace WebAPI.Models
{
    public class ProcessMessageRequest
    {
        public long SourceChannelId { get; set; }
        public long MessageId { get; set; }
    }
} 