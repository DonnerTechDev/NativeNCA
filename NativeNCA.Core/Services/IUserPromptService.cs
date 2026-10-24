using NativeNCA.Core.Models;

namespace NativeNCA.Core.Services;

public interface IUserPromptService
{
    /// <summary>Shows the confirmation overlay. Cancelling the token (client disconnected) closes it.</summary>
    Task<SigningPromptResult> PromptForSigningAsync(SigningPromptRequest request, CancellationToken cancellationToken = default);

    Task ShowNotificationAsync(string title, string message);
}
