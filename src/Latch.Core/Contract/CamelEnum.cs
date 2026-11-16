using System.Text.Json;
using System.Text.Json.Serialization;

namespace Latch.Core.Contract;

/// <summary>Enums travel as camelCase strings ("notRunning"); reading accepts any case.</summary>
public sealed class CamelEnum<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) where T : struct, Enum;
