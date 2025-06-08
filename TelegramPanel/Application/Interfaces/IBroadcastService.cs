// In Application/Interfaces/IBroadcastService.cs
public interface IBroadcastService
{
    Task<List<long>> GetAllActiveUserChatIdsAsync(CancellationToken cancellationToken = default);
}