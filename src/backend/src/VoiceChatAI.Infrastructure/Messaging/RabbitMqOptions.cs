namespace VoiceChatAI.Infrastructure.Messaging;

public class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";
    
    public string Host { get; set; } = "rabbitmq";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    
    // Queue names
    public string AudioChunksQueue { get; set; } = "audio_chunks";
    public string TranscriptsQueue { get; set; } = "transcripts";
    public string SummaryRequestsQueue { get; set; } = "summary_requests";
    public string AdviceRequestsQueue { get; set; } = "advice_requests";
}
