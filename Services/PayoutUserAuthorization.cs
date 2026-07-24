namespace VirtualTickets.Api.Services;

public static class PayoutUserAuthorization
{
    public const string ErrorCode = "PayoutUserNotConfigured";
    public const string ErrorMessage = "No valid virtual-ticket payout user is configured.";

    public static string RequireConfigured(Guid? configuredUserId)
    {
        if (!configuredUserId.HasValue || configuredUserId.Value == Guid.Empty)
        {
            throw CreateError();
        }

        return configuredUserId.Value.ToString("D");
    }

    public static string RequireActive(string configuredUserId, bool isActive) =>
        isActive ? configuredUserId : throw CreateError();

    private static TicketPayoutException CreateError() =>
        new(StatusCodes.Status500InternalServerError, ErrorCode, ErrorMessage);
}
