using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Latch.Core.Contract;

namespace Latch.Core;

/// <summary>
/// The one wrapper every API call runs through: parse `{id, method, args}`, dispatch, and answer with either the
/// result or an <see cref="ApiError"/>. Expected failures carry a code; anything else is logged and reported as
/// <see cref="ErrorCode.Unexpected"/>.
/// </summary>
public sealed class Rpc(ILatchApi api, Action<string, Exception> logUnexpected)
{
    private static readonly ContractJson J = ContractJson.Default;

    public async Task<string> HandleAsync(string requestJson)
    {
        long id = 0;
        string method = "";
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt64();
            method = root.GetProperty("method").GetString() ?? "";
            var args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
            var result = await DispatchAsync(method, args);
            return $"{{\"id\":{id},\"ok\":true,\"result\":{result}}}";
        }
        catch (ApiException e)
        {
            return ErrorResponse(id, e.Code);
        }
        catch (Exception e)
        {
            logUnexpected(method, e);
            return ErrorResponse(id, ErrorCode.Unexpected);
        }
    }

    public static string Event(string name, string dataJson) => $"{{\"event\":\"{name}\",\"data\":{dataJson}}}";

    public static string StateEvent(AppSnapshot s) => Event(ServerEvents.State, JsonSerializer.Serialize(s, J.AppSnapshot));

    public static string PromptEvent(PromptView? p) =>
        Event(ServerEvents.Prompt, p is null ? "null" : JsonSerializer.Serialize(p, J.PromptView));

    public static ApiError ToError(ErrorCode code)
    {
        var key = JsonNamingPolicy.CamelCase.ConvertName(code.ToString());
        FixAction? fix = code switch
        {
            ErrorCode.NeedsUnlock => new FixAction(FixKind.Unlock, Copy.Get("prompt.unlock")),
            ErrorCode.NotFound => null,
            ErrorCode.NotAProgram => null,
            ErrorCode.PinFormat => null,
            _ => new FixAction(FixKind.TryAgain, Copy.Get("common.tryAgain")),
        };
        return new ApiError(code, Copy.Get("errors." + key), fix);
    }

    private static string ErrorResponse(long id, ErrorCode code) =>
        $"{{\"id\":{id},\"ok\":false,\"error\":{JsonSerializer.Serialize(ToError(code), J.ApiError)}}}";

    private async Task<string> DispatchAsync(string method, JsonElement args) => method switch
    {
        "getState" => Out(await api.GetState(Empty.Value), J.AppSnapshot),
        "windowReady" => Out(await api.WindowReady(Empty.Value), J.Empty),
        "setPin" => Out(await api.SetPin(In(args, J.PinArgs)), J.Empty),
        "turnOnHello" => Out(await api.TurnOnHello(Empty.Value), J.Boolean),
        "turnOffHello" => Out(await api.TurnOffHello(Empty.Value), J.Empty),
        "addPasskey" => Out(await api.AddPasskey(Empty.Value), J.Boolean),
        "removePasskey" => Out(await api.RemovePasskey(Empty.Value), J.Empty),
        "finishSetup" => Out(await api.FinishSetup(Empty.Value), J.Empty),
        "listInstalledApps" => Out(await api.ListInstalledApps(Empty.Value), J.IReadOnlyListInstalledApp),
        "browseForProgram" => OutNullable(await api.BrowseForProgram(Empty.Value), J.InstalledApp),
        "setLockedApps" => Out(await api.SetLockedApps(In(args, J.SetLockedAppsArgs)), J.Empty),
        "removeApp" => Out(await api.RemoveApp(In(args, J.IdArgs)), J.Empty),
        "showApp" => Out(await api.ShowApp(In(args, J.IdArgs)), J.Empty),
        "getHistory" => Out(await api.GetHistory(Empty.Value), J.IReadOnlyListHistoryEntry),
        "dismissAttention" => Out(await api.DismissAttention(In(args, J.IdArgs)), J.Empty),
        "updateSettings" => Out(await api.UpdateSettings(In(args, J.SettingsPatch)), J.Empty),
        "requestUnlock" => Out(await api.RequestUnlock(In(args, J.PurposeArgs)), J.Boolean),
        "submitPin" => Out(await api.SubmitPin(In(args, J.PromptPinArgs)), J.UnlockResult),
        "usePasskey" => Out(await api.UsePasskey(In(args, J.PromptIdArgs)), J.UnlockResult),
        "useHello" => Out(await api.UseHello(In(args, J.PromptIdArgs)), J.UnlockResult),
        "cancelPrompt" => Out(await api.CancelPrompt(In(args, J.PromptIdArgs)), J.Empty),
        "promptPainted" => Out(await api.PromptPainted(In(args, J.PromptPaintedArgs)), J.Empty),
        _ => throw new InvalidOperationException("Unknown method " + method),
    };

    private static T In<T>(JsonElement args, JsonTypeInfo<T> type) =>
        args.ValueKind == JsonValueKind.Undefined ? throw new InvalidOperationException("Missing args") : args.Deserialize(type) ?? throw new InvalidOperationException("Null args");

    private static string Out<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.Serialize(value, type);

    private static string OutNullable<T>(T? value, JsonTypeInfo<T> type) where T : class =>
        value is null ? "null" : JsonSerializer.Serialize(value, type);
}
