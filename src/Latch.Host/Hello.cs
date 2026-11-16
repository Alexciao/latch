using Windows.Security.Credentials.UI;

namespace Latch.Host;

/// <summary>Windows Hello through UserConsentVerifier, parented to one of Latch's windows.</summary>
internal static class Hello
{
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            return await UserConsentVerifier.CheckAvailabilityAsync() == UserConsentVerifierAvailability.Available;
        }
        catch (Exception e)
        {
            Log.Error("hello availability", e);
            return false;
        }
    }

    public static async Task<bool> VerifyAsync(nint hwnd, string message)
    {
        try
        {
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(hwnd, message);
            return result == UserConsentVerificationResult.Verified;
        }
        catch (Exception e)
        {
            Log.Error("hello verify", e);
            return false;
        }
    }
}
